// One content widget per line end, shared by every feature that puts something beside a line's code (review
// keep/revert actions, review hint pills), so their items sit side by side instead of stacking on one spot.

import { monaco } from "./monaco-setup";
import "./line-end-lane.css";

/** Something shown after a line's end; `line` is read on every layout, so a tracked line can move. */
export interface LineEndItem {
  readonly dom: HTMLElement;
  /** Lower sorts first (leftmost). */
  readonly order: number;
  line(): number | null;
}

interface Row {
  readonly widget: monaco.editor.IContentWidget;
  /** The flex row inside the widget node, whose inline display Monaco overwrites. */
  readonly dom: HTMLElement;
  items: LineEndItem[];
}

interface Lane {
  readonly items: Set<LineEndItem>;
  /** Lays out once per task however many items or edits it saw. */
  readonly schedule: () => void;
}

const lanes = new WeakMap<monaco.editor.ICodeEditor, Lane>();

function createRow(editor: monaco.editor.ICodeEditor, line: number): Row {
  const node = document.createElement("div");
  const dom = node.appendChild(document.createElement("div"));
  dom.className = "weavie-line-end";
  const widget: monaco.editor.IContentWidget = {
    getId: () => `weavie.lineEnd.${line}`,
    getDomNode: () => node,
    getPosition: () => {
      const model = editor.getModel();
      return model === null || line > model.getLineCount()
        ? null
        : {
            position: { lineNumber: line, column: model.getLineMaxColumn(line) },
            preference: [monaco.editor.ContentWidgetPositionPreference.EXACT],
            // Right of any text injected at the line end, such as the blame annotation.
            positionAffinity: monaco.editor.PositionAffinity.Right,
          };
    },
    afterRender: (_position, coordinate) => {
      if (coordinate === null) return;
      const visibleRight = editor.getScrollLeft() + editor.getLayoutInfo().contentWidth;
      dom.style.height = `${editor.getOption(monaco.editor.EditorOption.lineHeight)}px`;
      dom.style.maxWidth = `${Math.max(0, visibleRight - coordinate.left - 8)}px`;
    },
  };
  editor.addContentWidget(widget);
  return { widget, dom, items: [] };
}

const sameItems = (a: readonly LineEndItem[], b: readonly LineEndItem[]): boolean =>
  a.length === b.length && a.every((item, index) => item === b[index]);

// Rows are keyed by line and kept across layouts: an edit only repositions them, and only a row whose line or
// items changed is rebuilt, so a large review doesn't re-add every widget per keystroke.
function createLane(editor: monaco.editor.ICodeEditor): Lane {
  const rows = new Map<number, Row>();
  const items = new Set<LineEndItem>();
  const layout = (): void => {
    const wanted = new Map<number, LineEndItem[]>();
    for (const item of items) {
      const line = item.line();
      if (line !== null) wanted.set(line, [...(wanted.get(line) ?? []), item]);
    }
    for (const [line, row] of rows) {
      if (wanted.has(line)) continue;
      editor.removeContentWidget(row.widget);
      rows.delete(line);
    }
    for (const [line, unsorted] of wanted) {
      const sorted = unsorted.sort((a, b) => a.order - b.order);
      const existing = rows.get(line);
      const row = existing ?? createRow(editor, line);
      rows.set(line, row);
      if (!sameItems(row.items, sorted)) {
        row.dom.replaceChildren(...sorted.map((item) => item.dom));
        row.items = sorted;
      }
      if (existing !== undefined) editor.layoutContentWidget(row.widget);
    }
  };
  let scheduled = false;
  const schedule = (): void => {
    if (scheduled) return;
    scheduled = true;
    queueMicrotask(() => {
      scheduled = false;
      layout();
    });
  };
  const onContent = editor.onDidChangeModelContent(schedule);
  const onModel = editor.onDidChangeModel(schedule);
  editor.onDidDispose(() => {
    onContent.dispose();
    onModel.dispose();
    lanes.delete(editor);
  });
  return { items, schedule };
}

/** Shows `item` at its line's end beside the editor's other line-end items; returns its removal. */
export function addLineEndItem(editor: monaco.editor.ICodeEditor, item: LineEndItem): () => void {
  let lane = lanes.get(editor);
  if (lane === undefined) {
    lane = createLane(editor);
    lanes.set(editor, lane);
  }
  const owner = lane;
  owner.items.add(item);
  owner.schedule();
  return () => {
    owner.items.delete(item);
    item.dom.remove();
    owner.schedule();
  };
}
