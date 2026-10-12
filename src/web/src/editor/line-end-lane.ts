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
  /** The row's unshrunk width, measured once per item change. */
  natural: number | null;
  /** The geometry the row was last fitted to; a render that matches it touches nothing. */
  fitted: string;
}

interface Lane {
  readonly items: Set<LineEndItem>;
  /** Lays out once per task however many items or edits it saw. */
  readonly schedule: () => void;
}

// Space kept between the row and the editor's visible right edge.
const EDGE_GAP = 8;

const lanes = new WeakMap<monaco.editor.ICodeEditor, Lane>();

interface Fit {
  readonly editor: monaco.editor.ICodeEditor;
  readonly line: number;
  readonly row: Row;
  readonly left: number;
  readonly room: number;
}

// Rows whose geometry changed during the current Monaco render, fitted together once it returns: measuring a
// line end inside a render re-enters it, nesting a full editor render per row.
let pending: Fit[] = [];

// Without room before the visible edge, the row slides left over text injected after the code (the blame
// annotation), never over the code itself, so its items stay on screen. One write-read-write pass for the batch.
function fitPending(): void {
  const fits = pending;
  pending = [];
  const unmeasured = fits.filter(({ row }) => row.natural === null);
  for (const { row } of unmeasured) {
    row.dom.style.maxWidth = "none";
    row.dom.style.width = "max-content";
  }
  for (const { row } of unmeasured) row.natural = row.dom.offsetWidth;
  const shifts = fits.map(({ editor, line, row, left, room }) => {
    const natural = row.natural ?? 0;
    const model = editor.getModel();
    if (natural <= room || model === null || line > model.getLineCount()) return 0;
    const codeEnd = editor.getOffsetForColumn(line, model.getLineMaxColumn(line));
    return Math.max(0, Math.min(natural - room, left - codeEnd));
  });
  shifts.forEach((shift, index) => {
    const { row, room } = fits[index] as Fit;
    row.dom.style.width = "";
    row.dom.style.transform = shift > 0 ? `translateX(${-shift}px)` : "";
    row.dom.classList.toggle("weavie-line-end-over", shift > 0);
    // Slid over, the backing runs to the very edge so no annotation text peeks past it.
    row.dom.style.maxWidth = `${Math.max(0, room + shift + (shift > 0 ? EDGE_GAP : 0))}px`;
  });
}

// Monaco renders widgets every scroll frame, so a row is refitted only when its items, line end, or the visible
// width changed.
function fitRow(editor: monaco.editor.ICodeEditor, line: number, row: Row, left: number): void {
  const lineHeight = editor.getOption(monaco.editor.EditorOption.lineHeight);
  const visibleRight = editor.getScrollLeft() + editor.getLayoutInfo().contentWidth - EDGE_GAP;
  const key = `${left}:${visibleRight}:${lineHeight}`;
  if (row.fitted === key && row.natural !== null) return;
  row.fitted = key;
  row.dom.style.height = `${lineHeight}px`;
  if (pending.length === 0) queueMicrotask(fitPending);
  pending.push({ editor, line, row, left, room: visibleRight - left });
}

function createRow(editor: monaco.editor.ICodeEditor, line: number): Row {
  const node = document.createElement("div");
  node.className = "weavie-line-end-widget";
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
      if (coordinate !== null) fitRow(editor, line, row, coordinate.left);
    },
  };
  const row: Row = { widget, dom, items: [], natural: null, fitted: "" };
  editor.addContentWidget(widget);
  return row;
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
        row.natural = null;
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
