import { monaco } from "../monaco-setup";

/** Review layout is shared by the live editor and its passive projection. */
export function reviewEditorOptions(): monaco.editor.IEditorOptions {
  const horizontalScrollbarSize =
    monaco.editor.EditorOptions.scrollbar.defaultValue.horizontalScrollbarSize;
  return {
    scrollBeyondLastLine: false,
    automaticLayout: false,
    smoothScrolling: false,
    overviewRulerLanes: 0,
    overviewRulerBorder: false,
    hideCursorInOverviewRuler: true,
    minimap: { enabled: false },
    folding: false,
    stickyScroll: { enabled: false },
    renderLineHighlightOnlyWhenFocus: true,
    scrollbar: { horizontalScrollbarSize, ignoreHorizontalScrollbarInContentHeight: true },
    padding: { top: 6, bottom: 6 + horizontalScrollbarSize },
  };
}
