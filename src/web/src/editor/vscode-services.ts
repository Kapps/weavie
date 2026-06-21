// Initializes the VSCode services that back Monaco (the services/editor slice of monaco-vscode-api). No
// container is passed to `initialize`, so no workbench/sidebar/panel is rendered. Runs once before the first
// editor is created.
//
// Service overrides enabled:
//  - theme + textmate + languages: coloring + semantic-highlighting substrate.
//  - model + editor: the editor service abstraction (active-editor state + "open this model"). Renders no
//    layout — it delegates how to show a file to the `openEditor` callback below, so weavie keeps full
//    control of its editors and file-opening.

import {
  IInstantiationService,
  StandaloneServices,
  initialize,
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

// Default VSCode extensions — declarative contributions, no extension-host JS. Each registers a language
// (file-extension associations) + TextMate grammar, giving an opened file its language id, which drives both
// syntax highlighting and LSP client selection. These curated packs are the LSP-backed languages and ship
// full language-configuration (comments/brackets/folding); every other language's highlighting comes from the
// broad loader below. Keep in sync with LanguageServerCatalog (Core).
//  - theme-defaults: the built-in Dark+/Light+/Modern color themes (so setTheme has something to load)
//  - typescript-basics / csharp / go: the TS/TSX, C#, and Go grammars + language configuration
import "@codingame/monaco-vscode-theme-defaults-default-extension";
import "@codingame/monaco-vscode-typescript-basics-default-extension";
import "@codingame/monaco-vscode-csharp-default-extension";
import "@codingame/monaco-vscode-go-default-extension";

// Semantic-highlighting consumer. monaco-languageclient registers a DocumentSemanticTokensProvider, but the
// editor-side feature that pulls tokens from it and repaints identifiers with the theme's
// `semanticTokenColors` is normally built by a workbench contribution on `onWillCreateCodeEditor` — an event
// that never fires for a standalone `monaco.editor.create` editor. So construct it ourselves after
// initialize() (see doInit); without it the provider is registered but never consumed.
import { DocumentSemanticTokensFeature } from "@codingame/monaco-vscode-api/vscode/vs/editor/contrib/semanticTokens/browser/documentSemanticTokens";
import { currentMonacoTheme, onMonacoThemeChanged } from "../theme";
import { applyMonacoTheme } from "../theme/monaco-theme";
import { registerBroadGrammars } from "./grammars/register-broad-grammars";
import { installHostFileProvider } from "./host-file-provider";

import textMateWorker from "@codingame/monaco-vscode-textmate-service-override/worker?worker";
// Workers. monaco-vscode-api uses a generic editor worker for most services and a dedicated worker for
// TextMate background tokenization (label "TextMateWorker"). The `monaco-editor` specifier is aliased to
// @codingame/monaco-vscode-editor-api (see package.json), so this is the vscode editor worker.
import editorWorker from "monaco-editor/esm/vs/editor/editor.worker?worker";

declare global {
  interface Window {
    /**
     * VSCode-service-layer state that must outlive a Vite hot reload. `initialize()` flips process-global
     * singletons in monaco-vscode-api that a hot reload never resets, so a module-local guard would reset
     * while the library's "already initialized" flag would not, and re-init would throw. Keeping the guard on
     * `window` matches its lifetime to what it guards. `activeEditor` lives here too: the editor-service
     * `openEditor` closure is captured once at first `initialize()` and never re-registered, so it must read
     * the current editor from shared state to survive a hot reload swapping the editor.
     */
    __WEAVIE_EDITOR_SERVICES__?: {
      initPromise?: Promise<void>;
      activeEditor?: monaco.editor.IStandaloneCodeEditor;
      openSink?: OpenEditorSink;
    };
  }
}

// How weavie opens a file the editor service asked for (go-to-def / peek / references). The editor host
// registers this so those targets flow through the tab store as a preview open (and reveal the range)
// instead of a bare setModel, keeping navigation from piling up persistent tabs.
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

  // No container argument → services/editor mode (no workbench, no layout control). The file service
  // override is listed explicitly for a deterministic dependency; it backs `file://` with an empty in-memory
  // layer that installHostFileProvider() fronts with the real host-backed provider. No autosave to disable:
  // the EditorAutoSave workbench contribution is never constructed in services/editor mode, so weavie's
  // debounced save() is the sole writer.
  await initialize({
    ...getThemeServiceOverride(),
    ...getTextmateServiceOverride(),
    ...getLanguagesServiceOverride(),
    ...getModelServiceOverride(),
    ...getEditorServiceOverride(openEditor),
    ...getFileServiceOverride(),
  });

  // Back the `file://` scheme with the host-backed provider in front of the empty in-memory layer. Must run
  // after initialize() and before any model resolves. Idempotent across hot reloads.
  installHostFileProvider();

  // Construct the document semantic-tokens feature (see its import note above): it pulls LSP semantic tokens
  // and repaints them through the active theme. Nothing else instantiates it in our services-only setup. Its
  // disposables hook the long-lived model/provider services, so it stays alive without us holding it.
  StandaloneServices.get(IInstantiationService).createInstance(DocumentSemanticTokensFeature);

  // Apply Weavie's active theme before any editor exists, so the first paint is the real theme (not a flash
  // of the default light theme). monaco.editor.defineTheme is a no-op under the theme service override, so
  // the theme is registered as an extension (see monaco-theme.ts); await it, then subscribe for live changes.
  const initialTheme = currentMonacoTheme();
  await applyMonacoTheme(initialTheme.id, initialTheme.theme);
  onMonacoThemeChanged((update) => {
    void applyMonacoTheme(update.id, update.theme);
  });

  // Broad highlighting: register every other language from the tm-grammars catalog. Must run after
  // initialize() and before any model is created, since Monaco resolves a model's language from its
  // extension at creation time.
  registerBroadGrammars();
}
