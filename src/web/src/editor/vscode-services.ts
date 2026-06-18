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

import { initialize } from "@codingame/monaco-vscode-api";
import getEditorServiceOverride, {
  type OpenEditor,
} from "@codingame/monaco-vscode-editor-service-override";
import getLanguagesServiceOverride from "@codingame/monaco-vscode-languages-service-override";
import getModelServiceOverride from "@codingame/monaco-vscode-model-service-override";
import getTextmateServiceOverride from "@codingame/monaco-vscode-textmate-service-override";
import getThemeServiceOverride from "@codingame/monaco-vscode-theme-service-override";
import * as monaco from "monaco-editor";

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
// `semanticTokenColors` lives in the full monaco-vscode-api — it is NOT in the editor-api ("monaco-editor")
// bundle, and none of the service overrides above import it. Without these two side-effect imports the
// provider is registered yet never consumed: the LSP's class/parameter/etc. coloring silently never
// applies and you see only TextMate (regex) colors. `documentSemanticTokens` registers the feature;
// `editorFeatures` is the workbench contribution that constructs registered editor features when an editor
// is created (same registerWorkbenchContribution2 / BlockRestore path the working TextMate tokenizer uses).
// Both must load before initialize().
import "@codingame/monaco-vscode-api/vscode/vs/editor/contrib/semanticTokens/browser/documentSemanticTokens";
import "@codingame/monaco-vscode-api/vscode/vs/workbench/contrib/codeEditor/browser/editorFeatures";
import { registerBroadGrammars } from "./grammars/register-broad-grammars";

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
    };
  }
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

  activeEditor.setModel(modelRef.object.textEditorModel);
  const selection = (options as { selection?: monaco.IRange } | undefined)?.selection;
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
  await initialize({
    ...getThemeServiceOverride(),
    ...getTextmateServiceOverride(),
    ...getLanguagesServiceOverride(),
    ...getModelServiceOverride(),
    ...getEditorServiceOverride(openEditor),
  });

  // Select a dark theme up front, before any editor exists, so the first editor paint is dark instead
  // of flashing the service layer's default (light) theme while the real one loads.
  monaco.editor.setTheme("vs-dark");

  // Broad highlighting: register every other language (grammar + extensions + generic config) from the
  // data-driven tm-grammars catalog. Must run after initialize() and before any model is created, since
  // Monaco resolves a model's language from its extension at creation time.
  registerBroadGrammars();
}
