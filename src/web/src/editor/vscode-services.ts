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
// model's language id). Without the matching extension a .cs/.go file falls back to plaintext: no
// highlighting and no language server. Keep this list in sync with LanguageServerCatalog (Core).
//  - theme-defaults: the built-in Dark+/Light+/Modern color themes (so setTheme has something to load)
//  - typescript-basics / csharp / go: the TS/JS, C#, and Go grammars + language configuration
import "@codingame/monaco-vscode-theme-defaults-default-extension";
import "@codingame/monaco-vscode-typescript-basics-default-extension";
import "@codingame/monaco-vscode-csharp-default-extension";
import "@codingame/monaco-vscode-go-default-extension";

import textMateWorker from "@codingame/monaco-vscode-textmate-service-override/worker?worker";
// Workers. monaco-vscode-api uses a generic editor worker for most services and a dedicated worker
// for TextMate background tokenization (label "TextMateWorker"). The `monaco-editor` specifier is
// aliased to @codingame/monaco-vscode-editor-api (see package.json), so this is the vscode editor worker.
import editorWorker from "monaco-editor/esm/vs/editor/editor.worker?worker";

let initPromise: Promise<void> | undefined;
let activeEditor: monaco.editor.IStandaloneCodeEditor | undefined;

/**
 * Registers weavie's editor as the surface the editor service opens files into. Called once the
 * editor is created; until then file-open requests are no-ops.
 */
export function registerActiveEditor(editor: monaco.editor.IStandaloneCodeEditor): void {
  activeEditor = editor;
}

// weavie owns layout: when the editor service is asked to open a model (go-to-def, peek, reveal-file),
// show it in our own editor pane and reveal the requested range. (Single pane for now; a tabbed model
// can replace this later — the point is the decision stays ours, not VSCode's.)
const openEditor: OpenEditor = (modelRef, options) => {
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
  if (initPromise === undefined) {
    initPromise = doInit();
  }
  return initPromise;
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
}
