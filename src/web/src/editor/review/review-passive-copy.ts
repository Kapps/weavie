import { getColumnOfNodeOffset } from "@codingame/monaco-vscode-api/vscode/vs/editor/browser/viewParts/viewLines/viewLine";
import type { CharacterMapping } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/viewLayout/viewLineRenderer";
import type { IModelLineProjection } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/viewModel/modelLineProjection";
import type { PassiveRow } from "./review-passive-document";

interface CopyLine {
  row: number;
  wrap: number;
  projection: IModelLineProjection | null;
  characters: CharacterMapping;
}

/** Source coordinates survive chunk boundaries, wrapping indentation, and injected text. */
export class PassiveCopyMap {
  private readonly lines = new WeakMap<HTMLElement, CopyLine>();

  public constructor(private readonly rows: readonly PassiveRow[]) {}

  public register(node: HTMLElement, line: CopyLine): void {
    this.lines.set(node, line);
  }

  public sourcePosition(node: Text, offset: number) {
    const position = this.position(node, offset);
    if (!position) return;
    const row = this.rows[position.row]!;
    return { lineNumber: row.line, column: position.column, removed: row.removed };
  }

  private position(node: Text, offset: number): { row: number; column: number } | undefined {
    const span = node.parentElement;
    const element = span?.closest<HTMLElement>(".view-line");
    const line = element && this.lines.get(element);
    if (!span || !line) return;
    const viewColumn = getColumnOfNodeOffset(line.characters, span, offset);
    const column = line.projection
      ? line.projection.getModelColumnOfViewPosition(line.wrap, viewColumn)
      : viewColumn;
    return {
      row: line.row,
      column: Math.max(1, Math.min(this.rows[line.row]!.text.length + 1, column)),
    };
  }

  private boundary(node: Node, offset: number, start: boolean) {
    if (node instanceof Text) return this.position(node, offset);
    const line = (node as Element).closest?.<HTMLElement>(".view-line");
    if (!line || !this.lines.has(line)) return;
    const point = document.createRange();
    point.setStart(node, offset);
    point.collapse(true);
    let before: Text | undefined;
    let after: Text | undefined;
    const walker = document.createTreeWalker(line, NodeFilter.SHOW_TEXT);
    for (let leaf = walker.nextNode(); leaf !== null; leaf = walker.nextNode()) {
      const text = leaf as Text;
      if (point.comparePoint(text, text.length) < 0) before = text;
      if (point.comparePoint(text, 0) > 0) {
        after = text;
        break;
      }
    }
    const text = start ? (after ?? before) : (before ?? after);
    return text && this.position(text, text === after ? 0 : text.length);
  }

  public copy(selection: Selection): string | undefined {
    if (selection.isCollapsed || selection.rangeCount === 0) return;
    const range = selection.getRangeAt(0);
    const boundaryStart = this.boundary(range.startContainer, range.startOffset, true);
    const boundaryEnd = this.boundary(range.endContainer, range.endOffset, false);
    let first: { node: Text; offset: number } | undefined;
    let last: { node: Text; offset: number } | undefined;
    const visit = (node: Text): void => {
      const line = node.parentElement?.closest<HTMLElement>(".view-line");
      if (!line || !this.lines.has(line)) return;
      const length = node.length;
      if (range.comparePoint(node, length) < 0 || range.comparePoint(node, 0) > 0) return;
      const from =
        node === range.startContainer
          ? range.startOffset
          : range.comparePoint(node, 0) < 0
            ? length
            : 0;
      const to =
        node === range.endContainer
          ? range.endOffset
          : range.comparePoint(node, length) > 0
            ? 0
            : length;
      if (from > to) return;
      first ??= { node, offset: from };
      last = { node, offset: to };
    };
    const root = range.commonAncestorContainer;
    if (root instanceof Text) visit(root);
    else {
      const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
      for (let node = walker.nextNode(); node !== null; node = walker.nextNode())
        visit(node as Text);
    }
    const start = boundaryStart ?? (first && this.position(first.node, first.offset));
    const end = boundaryEnd ?? (last && this.position(last.node, last.offset));
    if (!start || !end) return;
    return this.rows
      .slice(start.row, end.row + 1)
      .map((row, index, selected) =>
        row.text.slice(
          index === 0 ? start.column - 1 : 0,
          index === selected.length - 1 ? end.column - 1 : row.text.length,
        ),
      )
      .join("\n");
  }
}
