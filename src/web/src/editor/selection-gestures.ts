import type { SelectionActions } from "../agent/selection-actions";
import type { monaco } from "./monaco-setup";

/**
 * Shows `actions` for a selection once a gesture completes it (mouse-up or key-up after it changed), never per drag
 * step; scrolling it away or editing the text clears them. Only the actions it showed: an editor being built beside
 * another surface's selection leaves that one's alone.
 */
export function watchSelectionGestures(
  editor: monaco.editor.ICodeEditor,
  actions: Pick<SelectionActions, "clear" | "moved">,
  show: (selection: monaco.Selection, point: { x: number; y: number }) => void,
): () => void {
  const host = editor.getDomNode()!;
  let changed = false;
  let showing = false;
  const clear = (): void => {
    if (showing) actions.clear();
    showing = false;
  };
  // Where the shown selection's line sits in the view, to tell it moving from content elsewhere resizing.
  let shownTop: number | null = null;
  const top = (line: number): number => editor.getTopForLineNumber(line) - editor.getScrollTop();
  const completed = (): void => {
    if (!changed) return;
    changed = false;
    const selection = editor.getSelection();
    if (selection === null || selection.isEmpty()) {
      clear();
      return;
    }
    const position = editor.getScrolledVisiblePosition(selection.getPosition());
    if (position === null) return;
    const rect = host.getBoundingClientRect();
    shownTop = top(selection.getPosition().lineNumber);
    showing = true;
    show(selection, { x: rect.left + position.left, y: rect.top + position.top + position.height });
  };
  const disposables = [
    editor.onDidChangeCursorSelection(() => {
      changed = true;
      shownTop = null;
      clear();
    }),
    editor.onDidScrollChange((event) => {
      const line = editor.getSelection()?.getPosition().lineNumber;
      if (shownTop === null || line === undefined) return;
      if (event.scrollLeftChanged || top(line) !== shownTop) actions.moved();
    }),
    editor.onDidLayoutChange(() => {
      if (showing) actions.moved();
    }),
    editor.onMouseUp(completed),
    editor.onKeyUp(completed),
    editor.onDidChangeModelContent(clear),
  ];
  return () => {
    for (const disposable of disposables) disposable.dispose();
  };
}

/**
 * Calls `show` with the page selection once a pointer-up or key-up in `host` completes one wholly inside it, and
 * `clear` when that leaves none there.
 */
export function watchDomSelectionGestures(
  host: HTMLElement,
  show: (range: Range, text: string) => void,
  clear: () => void,
): () => void {
  const completed = (): void => {
    const selection = document.getSelection();
    if (
      selection === null ||
      selection.isCollapsed ||
      !host.contains(selection.anchorNode) ||
      !host.contains(selection.focusNode)
    ) {
      clear();
      return;
    }
    const text = selection.toString().trim();
    if (text.length > 0) show(selection.getRangeAt(0), text);
  };
  for (const type of ["pointerup", "keyup"]) host.addEventListener(type, completed);
  return () => {
    for (const type of ["pointerup", "keyup"]) host.removeEventListener(type, completed);
  };
}
