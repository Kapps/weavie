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

// Curated LSP-backed languages: each registers a language + TextMate grammar + full language-configuration;
// theme-defaults ships the built-in color themes. Every other language's highlighting comes from the broad
// loader below. Keep in sync with LanguageServerCatalog (Core).
import "@codingame/monaco-vscode-theme-defaults-default-extension";
import "@codingame/monaco-vscode-typescript-basics-default-extension";
import "@codingame/monaco-vscode-csharp-default-extension";
import "@codingame/monaco-vscode-go-default-extension";
import "@codingame/monaco-vscode-python-default-extension";
import "@codingame/monaco-vscode-rust-default-extension";

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
import { getClipboardServiceOverride } from "./clipboard-service";
import { registerBroadGrammars } from "./grammars/register-broad-grammars";
import { installHostFileProvider } from "./host-file-provider";
import { getNotificationServiceOverride } from "./notification-service";

declare global {
  interface Window {
    /**
     * VSCode-service state kept on `window` so it outlives a Vite hot reload, matching the lifetime of the
     * process-global singletons `initialize()` flips; a module-local guard would reinitialize them.
     */
    __WEAVIE_EDITOR_SERVICES__?: {
      initPromise?: Promise<void>;
    };
  }
}

window.__WEAVIE_EDITOR_SERVICES__ ??= {};
const servicesState = window.__WEAVIE_EDITOR_SERVICES__;

// Text navigation enters through ICodeEditorService's source-carrying handler in the editor host.
const openEditor: OpenEditor = async () => {
  throw new Error("Cannot navigate without an owned editor connection.");
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

  // Register the session-owned filesystem before services initialize. A dedicated scheme avoids the browser OS
  // canonicalizing host paths or treating session identity as a file:// UNC authority.
  installHostFileProvider();

  // No container → services/editor mode (no workbench). No autosave exists in this mode, so weavie's debounced
  // save() is the sole writer. This must run before any standalone `monaco.*` use — touching monaco
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
    ...getClipboardServiceOverride(),
  });

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
