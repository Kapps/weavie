import { StandaloneServices } from "@codingame/monaco-vscode-api";
import {
  IStorageService,
  StorageScope,
  StorageTarget,
} from "@codingame/monaco-vscode-api/services";
import * as monaco from "monaco-editor";
import { log } from "../bridge";
import {
  type EditorOptionsSpec,
  currentEditorOptions,
  onEditorOptionsChanged,
} from "../editor-options";
import { currentFonts, onFontsChanged } from "../fonts";
import { registerActiveEditor } from "./vscode-services";

// Workers + the VSCode service substrate are wired in `vscode-services.ts` (initEditorServices),
// which must run before any editor is created. TypeScript/JS intelligence now comes from a real LSP
// server over the bridge (see lsp/lsp-client.ts), not Monaco's bundled ts.worker — so that worker is
// intentionally gone (spec §9: replace Monaco's in-browser TS immediately).

export function createEditor(container: HTMLElement): monaco.editor.IStandaloneCodeEditor {
  // The editor starts with NO document — an empty pane until the host opens a file (or, after a hot reload,
  // the editor host reattaches the file that was open before; see editor-host.ts). File models are created
  // on open with a real `file://` URI under the workspace: tsserver-family servers (tsgo) give loose
  // inmemory:/untitled: docs only partial service — they publish diagnostics for file:// docs, not in-memory
  // ones — so a file:// URI makes it a project file and squiggles flow.
  // Typography + editor behavior are user settings resolved by the host (global font.* + editor.font.*
  // overrides; the editor.* options), injected before navigation so we mount at the right font/options,
  // and live-updated below.
  const font = currentFonts().editor;
  const editorOptions = currentEditorOptions();
  const editor = monaco.editor.create(container, {
    model: null,
    // No `theme` here on purpose: the active theme is global and owned by the theme controller (registered
    // + applied in vscode-services before any editor is created). Passing a `theme` option re-calls setTheme
    // and would clobber the active theme back to that value — which is exactly what left Monaco on vs-dark.
    fontSize: font.size,
    fontFamily: font.family,
    fontWeight: font.weight,
    automaticLayout: true,
    // Editor behavior (minimap, inlay hints, word wrap, hover delay, …) — every one is a typed Weavie
    // setting now (Core EditorSettings), not a hardcoded constant.
    ...toMonacoOptions(editorOptions),
  });
  applySuggestExpandDocs(editorOptions.suggestExpandDocs);

  // Apply live font changes (Monaco re-lays out on updateOptions); drop the subscription with the editor.
  const offFonts = onFontsChanged((config) =>
    editor.updateOptions({
      fontFamily: config.editor.family,
      fontSize: config.editor.size,
      fontWeight: config.editor.weight,
    }),
  );
  editor.onDidDispose(offFonts);

  // Apply live editor-option changes the same way fonts do.
  const offEditorOptions = onEditorOptionsChanged((next) => {
    editor.updateOptions(toMonacoOptions(next));
    applySuggestExpandDocs(next.suggestExpandDocs);
  });
  editor.onDidDispose(offEditorOptions);

  // The editor service opens go-to-def / reveal-file targets through this editor (we own layout).
  registerActiveEditor(editor);
  return editor;
}

// Maps Weavie's flat editor-option settings onto Monaco's nested IEditorOptions shape. The string-union
// values are authored to match Monaco's option types exactly, so this is a straight structural mapping.
function toMonacoOptions(o: EditorOptionsSpec): monaco.editor.IEditorOptions {
  return {
    inlayHints: { enabled: o.inlayHints },
    minimap: { enabled: o.minimap },
    bracketPairColorization: { enabled: o.bracketPairColorization },
    smoothScrolling: o.smoothScrolling,
    cursorSmoothCaretAnimation: o.cursorSmoothCaretAnimation,
    renderWhitespace: o.renderWhitespace,
    scrollBeyondLastLine: o.scrollBeyondLastLine,
    wordWrap: o.wordWrap,
    lineNumbers: o.lineNumbers,
    cursorBlinking: o.cursorBlinking,
    renderLineHighlight: o.renderLineHighlight,
    stickyScroll: { enabled: o.stickyScroll },
    fontLigatures: o.fontLigatures,
    guides: { indentation: o.indentGuides },
    hover: { delay: o.hoverDelay },
  };
}

// Auto-expand the suggest-widget documentation flyout. Monaco has NO editor option for this — the widget
// only persists the user's manual expand/collapse toggle under this storage key — so we seed that key from
// the setting. It's read when the widget is constructed (first completion), so this honors the initial /
// default state; changing the setting mid-session may need the widget to re-open to take effect. Best-
// effort but observable: a failure logs (these suggest internals can shift between versions) rather than
// silently passing or breaking editor creation.
function applySuggestExpandDocs(enabled: boolean): void {
  try {
    StandaloneServices.get(IStorageService).store(
      "expandSuggestionDocs",
      enabled,
      StorageScope.PROFILE,
      StorageTarget.USER,
    );
  } catch (err) {
    log("warn", `editor.suggest.expandDocs: could not seed storage (${String(err)})`);
  }
}

export { monaco };
