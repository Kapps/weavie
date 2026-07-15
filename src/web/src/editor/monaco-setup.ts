import { StandaloneServices } from "@codingame/monaco-vscode-api";
import {
  IStorageService,
  StorageScope,
  StorageTarget,
} from "@codingame/monaco-vscode-api/services";
import * as monaco from "monaco-editor";
import { log } from "../bridge";
import {
  currentEditorOptions,
  type EditorOptionsSpec,
  onEditorOptionsChanged,
} from "../editor-options";
import { currentFonts, onFontsChanged } from "../fonts";
import { registerActiveEditor } from "./vscode-services";

// Workers + the VSCode service substrate are wired in `vscode-services.ts` (initEditorServices), which must run
// before any editor is created. TS/JS intelligence comes from a real LSP server (lsp/lsp-client.ts), not ts.worker.

export function createEditor(container: HTMLElement): monaco.editor.IStandaloneCodeEditor {
  // Starts with no document (empty pane until the host opens a file). File models open with a real `file://`
  // URI so tsserver-family servers treat them as project files and publish diagnostics. Typography + behavior
  // are user settings, live-updated below.
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
    // Weavie owns the right-click menu (App wires it on the editor container) so it's consistent with the rest
    // of the app and command-driven; Monaco's own menu would double up.
    contextmenu: false,
    // Render overflow widgets (suggest, hover) in a viewport-fixed node so they aren't clipped at a split
    // pane's edge. Safe because panes tile with absolute left/top, no transform ancestor to trap the node.
    fixedOverflowWidgets: true,
    // Compact gutter: 3 line-number chars (auto-grows past 999 lines) + a 6px decoration strip trims Monaco's
    // wide default band, while keeping the glyph margin for the lightbulb and change-tracking bars.
    lineNumbersMinChars: 3,
    lineDecorationsWidth: 6,
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

  // Monaco measures its FontInfo when the editor is created; the bundled editor webfont (Go Mono) only loads
  // once text first renders in it, so the initial metrics are the fallback font's and the caret ends up
  // misaligned from the glyphs (drifting ~¼px per column). Remeasure once fonts are ready and whenever a font
  // finishes loading, so the caret tracks the real glyph advances. remeasureFonts() relays out every editor.
  const remeasure = (): void => monaco.editor.remeasureFonts();
  void document.fonts.ready.then(remeasure);
  document.fonts.addEventListener("loadingdone", remeasure);
  editor.onDidDispose(() => document.fonts.removeEventListener("loadingdone", remeasure));

  // Apply live editor-option changes the same way fonts do.
  const offEditorOptions = onEditorOptionsChanged((next) => {
    editor.updateOptions(toMonacoOptions(next));
    applySuggestExpandDocs(next.suggestExpandDocs);
  });
  editor.onDidDispose(offEditorOptions);

  // The editor service opens go-to-def / reveal-file targets through this editor.
  registerActiveEditor(editor);

  // Publish the live editor for e2e / diagnostics introspection (read-only); a rebuild overwrites it. See
  // global.d.ts.
  window.__WEAVIE_EDITOR__ = editor;
  // Publish the monaco namespace too, so e2e can register the LSP-backed providers (rename, code actions) the
  // harness has no language server to supply — the only way to drive those UX paths deterministically.
  window.__WEAVIE_MONACO__ = monaco;
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

// Auto-expand the suggest-widget docs flyout. No editor option exists, so seed the storage key the widget reads
// on construction; a mid-session change may need the widget to re-open. Best-effort: a failure logs, not throws.
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
