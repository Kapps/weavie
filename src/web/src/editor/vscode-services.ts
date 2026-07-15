// VSCode services backing Monaco (services/editor slice of monaco-vscode-api). No container is passed to
// `initialize`, so no workbench/layout renders — the editor-service override delegates file-opening to the
// `openEditor` callback below, keeping weavie in full control of its editors.

import {
  IInstantiationService,
  initialize,
  StandaloneServices,
} from "@codingame/monaco-vscode-api";
import getEditorServiceOverride, {
  type OpenEditor,
} from "@codingame/monaco-vscode-editor-service-override";
import getFileServiceOverride from "@codingame/monaco-vscode-files-service-override";
import getLanguagesServiceOverride from "@codingame/monaco-vscode-languages-service-override";
import getModelServiceOverride from "@codingame/monaco-vscode-model-service-override";
import getTextmateServiceOverride from "@codingame/monaco-vscode-textmate-service-override";
import getThemeServiceOverride from "@codingame/monaco-vscode-theme-service-override";
import type * as monaco from "monaco-editor";

// Curated LSP-backed languages: each registers a language + TextMate grammar + full language-configuration;
// theme-defaults ships the built-in color themes. Every other language's highlighting comes from the broad
// loader below. Keep in sync with LanguageServerCatalog (Core).
import "@codingame/monaco-vscode-theme-defaults-default-extension";
import "@codingame/monaco-vscode-typescript-basics-default-extension";
import "@codingame/monaco-vscode-csharp-default-extension";
import "@codingame/monaco-vscode-go-default-extension";

// Semantic-highlighting consumer. The feature that paints LSP tokens is normally built by a workbench
// contribution on `onWillCreateCodeEditor`, which never fires for a standalone editor — so we construct it
// ourselves in doInit(), else the provider is registered but never consumed.
import { DocumentSemanticTokensFeature } from "@codingame/monaco-vscode-api/vscode/vs/editor/contrib/semanticTokens/browser/documentSemanticTokens";
import textMateWorker from "@codingame/monaco-vscode-textmate-service-override/worker?worker";
// Generic editor worker for most services; the dedicated TextMate worker (label "TextMateWorker") handles
// background tokenization. `monaco-editor` is aliased to the vscode editor-api (see package.json).
import editorWorker from "monaco-editor/esm/vs/editor/editor.worker?worker";
import { log } from "../bridge";
import { notify } from "../notify/notify";
import { currentMonacoTheme, onMonacoThemeChanged } from "../theme";
import { applyMonacoTheme } from "../theme/monaco-theme";
import { registerBroadGrammars } from "./grammars/register-broad-grammars";
import { installHostFileProvider } from "./host-file-provider";
import { getNotificationServiceOverride } from "./notification-service";

declare global {
  interface Window {
    /**
     * VSCode-service state kept on `window` so it outlives a Vite hot reload, matching the lifetime of the
     * process-global singletons `initialize()` flips (a module-local guard would reset and re-init would
     * throw). `activeEditor` lives here too so the once-captured `openEditor` closure reads the current editor.
     */
    __WEAVIE_EDITOR_SERVICES__?: {
      initPromise?: Promise<void>;
      activeEditor?: monaco.editor.IStandaloneCodeEditor;
      openSink?: OpenEditorSink;
    };
  }
}

// Routes editor-service file-opens (go-to-def / peek / references) through the tab store as a preview open,
// so navigation doesn't pile up persistent tabs. Registered by the editor host.
export type OpenEditorSink = (uri: monaco.Uri, selection: monaco.IRange | undefined) => void;

/** Registers the sink the editor service routes file-opens through (called once the editor host is up). */
export function setOpenEditorSink(sink: OpenEditorSink): void {
  servicesState.openSink = sink;
}

// First module instance creates the state; later hot-reloaded instances reuse the same object.
window.__WEAVIE_EDITOR_SERVICES__ ??= {};
const servicesState = window.__WEAVIE_EDITOR_SERVICES__;

/**
 * Registers weavie's editor as the surface the editor service opens files into. Until called, file-open
 * requests are no-ops.
 */
export function registerActiveEditor(editor: monaco.editor.IStandaloneCodeEditor): void {
  servicesState.activeEditor = editor;
}

/** The editor weavie renders files into — focus-independent, so palette/keyboard commands can reach it. */
export function activeCodeEditor(): monaco.editor.IStandaloneCodeEditor | undefined {
  return servicesState.activeEditor;
}

// weavie owns layout: when the editor service is asked to open a model (go-to-def, peek, reveal-file), show
// it in our own editor pane and reveal the requested range.
const openEditor: OpenEditor = (modelRef, options) => {
  const activeEditor = servicesState.activeEditor;
  if (activeEditor === undefined) {
    return Promise.resolve(undefined);
  }
  const selection = (options as { selection?: monaco.IRange } | undefined)?.selection;

  // When the editor host has registered its sink, route through the tab store (preview open + range reveal).
  const sink = servicesState.openSink;
  if (sink !== undefined) {
    sink(modelRef.object.textEditorModel.uri, selection);
    return Promise.resolve(activeEditor);
  }

  // Fallback before the host is up or in plain-browser dev: bare setModel.
  activeEditor.setModel(modelRef.object.textEditorModel);
  if (selection !== undefined) {
    activeEditor.setSelection(selection);
    activeEditor.revealRangeInCenterIfOutsideViewport(selection);
  }
  activeEditor.focus();
  return Promise.resolve(activeEditor);
};

/** Initializes the VSCode services backing Monaco. Idempotent — subsequent calls return the same promise. */
export function initEditorServices(): Promise<void> {
  servicesState.initPromise ??= doInit();
  return servicesState.initPromise;
}

async function doInit(): Promise<void> {
  self.MonacoEnvironment = {
    getWorker(_workerId: string, label: string): Worker {
      if (label === "TextMateWorker") {
        return new textMateWorker();
      }
      return new editorWorker();
    },
  };

  // No container → services/editor mode (no workbench). The file-service override backs `file://` with an
  // empty in-memory layer that installHostFileProvider() fronts. No autosave exists in this mode, so weavie's
  // debounced save() is the sole writer. This must run before any standalone `monaco.*` use — touching monaco
  // auto-initializes the services without these overrides, after which this call throws "already initialized".
  // Every monaco-touching entry point (the editor host, the LSP client) funnels through initEditorServices first.
  await initialize({
    ...getThemeServiceOverride(),
    ...getTextmateServiceOverride(),
    ...getLanguagesServiceOverride(),
    ...getModelServiceOverride(),
    ...getEditorServiceOverride(openEditor),
    ...getFileServiceOverride(),
    // Route Monaco's INotificationService (failed rename / code action) to Weavie toasts; the standalone
    // default only logs to the console, leaving a failed refactor invisible.
    ...getNotificationServiceOverride(),
  });

  // Front `file://` with the host-backed provider, after initialize() and before any model resolves.
  // Idempotent across hot reloads.
  installHostFileProvider();

  // Construct the semantic-tokens feature (see its import note); its disposables hook long-lived services, so
  // it stays alive without us holding it.
  StandaloneServices.get(IInstantiationService).createInstance(DocumentSemanticTokensFeature);

  // Apply the active theme before any editor exists, so the first paint isn't a flash of the default light
  // theme. The theme is registered as an extension (see monaco-theme.ts); await it, then track live changes.
  const initialTheme = currentMonacoTheme();
  await applyMonacoTheme(initialTheme.id, initialTheme.theme);
  onMonacoThemeChanged((update) => {
    applyMonacoTheme(update.id, update.theme).catch((err: unknown) => {
      // A live theme push that fails to register would otherwise silently keep the old theme.
      const message = err instanceof Error ? err.message : String(err);
      log("error", `theme: applying '${update.id}' failed: ${message}`);
      notify("warn", `Couldn't apply the theme: ${message}`);
    });
  });

  // Broad highlighting for every other language. Must run before any model is created, since Monaco resolves
  // a model's language from its extension at creation time.
  registerBroadGrammars();
}
