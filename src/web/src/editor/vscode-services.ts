// Initializes the VSCode services that back Monaco, per the theming+LSP spec (§7): the *services /
// editor* slice of @codingame/monaco-vscode-api. We pass no container to `initialize`, so no
// workbench / activity-bar / sidebar / panel is ever rendered (the spec's hard-no). Runs once, before
// the first editor is created.
//
// Service overrides we enable (all guardrail-safe per §18 — none are workbench/views/configuration/
// keybindings/extension-host):
//  - theme + textmate + languages: faithful coloring + semantic highlighting substrate (§7 core).
//  - model + editor: the editor SERVICE abstraction (active/visible-editor state + "open this model").
//    Real LSP features need it — pull diagnostics key off the active editor, and go-to-def/peek ask
//    the service to open a target. It renders NO layout: it delegates *how* to show a file to our
//    `openEditor` callback below, so weavie keeps full control of its editors and file-opening.

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

// Default VSCode extensions — declarative contributions, no extension-host JS. Each registers a
// language (its file-extension associations) + TextMate grammar, which is what gives an opened file its
// language id — driving both syntax highlighting AND the LSP client selection (lsp-client keys off the
// model's language id). These curated packs are the LSP-backed languages: they ship full
// language-configuration (comments/brackets/folding) and stay authoritative. Every OTHER language's
// highlighting comes from the data-driven broad loader below (registerBroadGrammars). Keep these in sync
// with LanguageServerCatalog (Core).
//  - theme-defaults: the built-in Dark+/Light+/Modern color themes (so setTheme has something to load)
//  - typescript-basics / csharp / go: the TS/TSX, C#, and Go grammars + language configuration
import "@codingame/monaco-vscode-theme-defaults-default-extension";
import "@codingame/monaco-vscode-typescript-basics-default-extension";
import "@codingame/monaco-vscode-csharp-default-extension";
import "@codingame/monaco-vscode-go-default-extension";

// Semantic-highlighting CONSUMER. monaco-languageclient registers a DocumentSemanticTokensProvider, but
// the editor-side feature that pulls tokens from it and repaints identifiers with the theme's
// `semanticTokenColors` lives in the full monaco-vscode-api — it's not in the editor-api ("monaco-editor")
// bundle, and no service override constructs it. It's a `registerEditorFeature` feature, normally built by
// the EditorFeaturesInstantiator workbench contribution on `onWillCreateCodeEditor` — but that event never
// fires for a standalone `monaco.editor.create` editor (and at BlockRestore no editor exists yet), so we
// construct it ourselves right after initialize() (see doInit). Without it the provider is registered yet
// never consumed: the LSP's class/parameter/etc. coloring never applies and only TextMate colors show.
import { DocumentSemanticTokensFeature } from "@codingame/monaco-vscode-api/vscode/vs/editor/contrib/semanticTokens/browser/documentSemanticTokens";
import { currentMonacoTheme, onMonacoThemeChanged } from "../theme";
import { applyMonacoTheme } from "../theme/monaco-theme";
import { registerBroadGrammars } from "./grammars/register-broad-grammars";
import { installHostFileProvider } from "./host-file-provider";

import textMateWorker from "@codingame/monaco-vscode-textmate-service-override/worker?worker";
// Workers. monaco-vscode-api uses a generic editor worker for most services and a dedicated worker
// for TextMate background tokenization (label "TextMateWorker"). The `monaco-editor` specifier is
// aliased to @codingame/monaco-vscode-editor-api (see package.json), so this is the vscode editor worker.
import editorWorker from "monaco-editor/esm/vs/editor/editor.worker?worker";

declare global {
  interface Window {
    /**
     * VSCode-service-layer state that must outlive a Vite hot reload. `initialize()` flips
     * process-global singletons in monaco-vscode-api (lifecycle.js: `servicesInitialized` +
     * `serviceInitializedBarrier`) that a hot reload never resets. When an edit anywhere in the editor
     * chunk propagates up to App.tsx's self-accepting SolidJS boundary, THIS module gets a fresh
     * instance — so a module-local guard would be reset while the library's "already initialized" flag
     * is not, and the re-init would throw "Services are already initialized" and blank the editor. Keep
     * the guard on `window` so its lifetime matches what it guards (mirrors the library's own
     * `window.monacoVscodeApiBuildId`). `activeEditor` lives here too: the editor-service `openEditor`
     * closure is captured once at first `initialize()` and is never re-registered, so it must read the
     * current editor from shared state to keep working after a hot reload swaps the editor.
     */
    __WEAVIE_EDITOR_SERVICES__?: {
      initPromise?: Promise<void>;
      activeEditor?: monaco.editor.IStandaloneCodeEditor;
      openSink?: OpenEditorSink;
    };
  }
}

// How weavie opens a file the editor service asked for (go-to-def / peek / references). The editor host
// registers this so those targets flow through the tab store as a PREVIEW open (and reveal the range),
// instead of a bare setModel — keeping navigation from piling up persistent tabs. Re-registered per host
// build (it closes over the current editor), like `activeEditor`, so it survives a hot reload.
export type OpenEditorSink = (uri: monaco.Uri, selection: monaco.IRange | undefined) => void;

/** Registers the sink the editor service routes file-opens through (called once the editor host is up). */
export function setOpenEditorSink(sink: OpenEditorSink): void {
  servicesState.openSink = sink;
}

// First module instance creates the state; every later (hot-reloaded) instance reuses the same object.
window.__WEAVIE_EDITOR_SERVICES__ ??= {};
const servicesState = window.__WEAVIE_EDITOR_SERVICES__;

/**
 * Registers weavie's editor as the surface the editor service opens files into. Called once the
 * editor is created; until then file-open requests are no-ops.
 */
export function registerActiveEditor(editor: monaco.editor.IStandaloneCodeEditor): void {
  servicesState.activeEditor = editor;
}

// weavie owns layout: when the editor service is asked to open a model (go-to-def, peek, reveal-file),
// show it in our own editor pane and reveal the requested range. (Single pane for now; a tabbed model
// can replace this later — the point is the decision stays ours, not VSCode's.)
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

  // Fallback before the host is up / in plain-browser dev: bare setModel.
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

  // No container argument → services/editor mode (no workbench, no layout control).
  //
  // The file service override is added EXPLICITLY (it was already pulled in transitively, but listing it
  // makes the dependency deterministic). It backs `file://` with an OverlayFileSystemProvider whose only
  // built-in layer is an empty in-memory FS — installHostFileProvider() below registers weavie's real,
  // host-backed provider in front of it so `file://` models resolve against disk. Note: no autosave to
  // disable — `files.autoSave` is driven by the EditorAutoSave *workbench* contribution, which is never
  // constructed in services/editor mode; weavie's debounced save() is the sole writer. (Adding a
  // configuration-service-override to flip files.autoSave would also breach the §18 guardrail.)
  await initialize({
    ...getThemeServiceOverride(),
    ...getTextmateServiceOverride(),
    ...getLanguagesServiceOverride(),
    ...getModelServiceOverride(),
    ...getEditorServiceOverride(openEditor),
    ...getFileServiceOverride(),
  });

  // Back the `file://` scheme with the host-backed provider (real disk via the C# bridge), in front of the
  // empty in-memory layer. Must run after initialize() (the file service must exist) and before any model
  // resolves. Idempotent across hot reloads.
  installHostFileProvider();

  // Construct the document semantic-tokens feature (see its import note above): it watches every model,
  // pulls LSP semantic tokens, and repaints them through the active theme. Nothing else instantiates it in
  // our services-only setup, so without this the registered provider goes unconsumed. Its disposables hook
  // the (long-lived) model/provider services, so it stays alive without holding the instance ourselves.
  StandaloneServices.get(IInstantiationService).createInstance(DocumentSemanticTokensFeature);

  // Apply Weavie's active theme before any editor exists, so the first editor paint is the real theme
  // (not a flash of the service layer's default light theme). monaco.editor.defineTheme is a no-op under
  // the theme service override, so the theme is registered as an extension (see monaco-theme.ts): we await
  // its registration, then subscribe so later active-theme / override changes re-theme Monaco live.
  const initialTheme = currentMonacoTheme();
  await applyMonacoTheme(initialTheme.id, initialTheme.theme);
  onMonacoThemeChanged((update) => {
    void applyMonacoTheme(update.id, update.theme);
  });

  // Broad highlighting: register every other language (grammar + extensions + generic config) from the
  // data-driven tm-grammars catalog. Must run after initialize() and before any model is created, since
  // Monaco resolves a model's language from its extension at creation time.
  registerBroadGrammars();
}
