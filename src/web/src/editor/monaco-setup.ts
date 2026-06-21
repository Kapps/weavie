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

// Workers + the VSCode service substrate are wired in `vscode-services.ts` (initEditorServices), which must
// run before any editor is created. TypeScript/JS intelligence comes from a real LSP server over the bridge
// (see lsp/lsp-client.ts), not Monaco's bundled ts.worker.

export function createEditor(container: HTMLElement): monaco.editor.IStandaloneCodeEditor {
  // The editor starts with no document — an empty pane until the host opens a file. File models are created
  // on open with a real `file://` URI under the workspace: tsserver-family servers (tsgo) publish
  // diagnostics for file:// docs, not in-memory ones, so a file:// URI makes it a project file and squiggles
  // flow. Typography + editor behavior are user settings resolved by the host and live-updated below.
  const font = currentFonts().editor;
  const editorOptions = currentEditorOptions();
  const editor = monaco.editor.create(container, {
    model: null,
    // No `theme` here on purpose: the active theme is global and owned by the theme controller. Passing a
    // `theme` option re-calls setTheme and would clobber the active theme back to that value.
    fontSize: font.size,
    fontFamily: font.family,
    fontWeight: font.weight,
    automaticLayout: true,
    // Render overflow widgets (suggest list, parameter hints, hover) in a viewport-fixed DOM node so they
    // aren't clipped at the editor pane's edge in a split layout. Safe because panes are tiled with absolute
    // left/top (no transform ancestor that would trap the fixed-position node).
    fixedOverflowWidgets: true,
    // Editor behavior (minimap, inlay hints, word wrap, hover delay, …) — each a typed Weavie setting.
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

  // The editor service opens go-to-def / reveal-file targets through this editor.
  registerActiveEditor(editor);
  return editor;
}

// Maps Weavie's flat editor-option settings onto Monaco's nested IEditorOptions shape. The string-union
// values match Monaco's option types exactly, so this is a straight structural mapping.
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

// Auto-expand the suggest-widget documentation flyout. Monaco has no editor option for this, so seed the
// storage key the widget reads when constructed (first completion). Changing the setting mid-session may need
// the widget to re-open to take effect. Best-effort: a failure logs rather than breaking editor creation.
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
