// Pure geometry for the layout renderer: turn a layout tree into pane rectangles + splitter handles, and
// adjust a split's weights when a handle is dragged. Percentages (0-100) throughout so the renderer can
// position everything absolutely.

import type { LayoutNode, SplitDir } from "./types";

export interface Rect {
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface SplitterInfo {
  dir: SplitDir; // "row" => vertical handle (col-resize); "column" => horizontal handle (row-resize)
  path: number[]; // child-index path from the root to the split node this handle belongs to
  index: number; // the boundary between child[index] and child[index + 1]
  x: number; // handle position (%), a line along the split's cross-axis
  y: number;
  cross: number; // length of the handle along the cross-axis (%)
  axisStart: number; // the split's extent along its own axis (%), for mapping a drag to a fraction
  axisSize: number;
}

const FULL: Rect = { x: 0, y: 0, w: 100, h: 100 };
const MIN_FRACTION = 0.05;

function weightSum(weights: number[]): number {
  const total = weights.reduce((a, b) => a + b, 0);
  return total > 0 ? total : 1;
}

function childRect(parent: Rect, dir: SplitDir, pos: number, size: number): Rect {
  return dir === "row"
    ? { x: pos, y: parent.y, w: size, h: parent.h }
    : { x: parent.x, y: pos, w: parent.w, h: size };
}

/** Pane kinds in DFS order (left-to-right / top-to-bottom): the order Ctrl+1..9 selects and the number shown on each pane. */
export function paneOrder(root: LayoutNode): string[] {
  const out: string[] = [];
  collectPanes(root, out);
  return out;
}

function collectPanes(node: LayoutNode, out: string[]): void {
  if (node.type === "pane") {
    out.push(node.kind);
    return;
  }
  for (const child of node.children) {
    collectPanes(child, out);
  }
}

/** Maps each pane kind to its rectangle. Kinds are singletons in v1, so kind is a stable slot key. */
export function computeRects(root: LayoutNode): Map<string, Rect> {
  const out = new Map<string, Rect>();
  walkRects(root, FULL, out);
  return out;
}

function walkRects(node: LayoutNode, rect: Rect, out: Map<string, Rect>): void {
  if (node.type === "pane") {
    out.set(node.kind, rect);
    return;
  }
  const total = weightSum(node.weights);
  const span = node.dir === "row" ? rect.w : rect.h;
  let pos = node.dir === "row" ? rect.x : rect.y;
  node.children.forEach((child, i) => {
    const size = (span * (node.weights[i] ?? 1)) / total;
    walkRects(child, childRect(rect, node.dir, pos, size), out);
    pos += size;
  });
}

/** One draggable handle per internal boundary of every split, with the geometry the drag needs. */
export function computeSplitters(root: LayoutNode): SplitterInfo[] {
  const out: SplitterInfo[] = [];
  walkSplitters(root, FULL, [], out);
  return out;
}

function walkSplitters(node: LayoutNode, rect: Rect, path: number[], out: SplitterInfo[]): void {
  if (node.type !== "split") {
    return;
  }
  const total = weightSum(node.weights);
  const span = node.dir === "row" ? rect.w : rect.h;
  const axisStart = node.dir === "row" ? rect.x : rect.y;
  let pos = axisStart;
  node.children.forEach((child, i) => {
    const size = (span * (node.weights[i] ?? 1)) / total;
    walkSplitters(child, childRect(rect, node.dir, pos, size), [...path, i], out);
    pos += size;
    if (i < node.children.length - 1) {
      out.push({
        dir: node.dir,
        path,
        index: i,
        x: node.dir === "row" ? pos : rect.x,
        y: node.dir === "row" ? rect.y : pos,
        cross: node.dir === "row" ? rect.h : rect.w,
        axisStart,
        axisSize: span,
      });
    }
  });
}

/**
 * Returns a new tree with the split at `path` resized so the boundary after child `index` sits at
 * `fraction` (0-1) of the split's own extent. Refuses to shrink either neighbour below a minimum.
 */
export function setBoundary(
  root: LayoutNode,
  path: number[],
  index: number,
  fraction: number,
): LayoutNode {
  return mapAtPath(root, path, (node) => {
    if (node.type !== "split") {
      return node;
    }
    const total = weightSum(node.weights);
    let cumulative = 0;
    for (let i = 0; i <= index; i++) {
      cumulative += node.weights[i] ?? 0;
    }
    const target = Math.min(1, Math.max(0, fraction)) * total;
    const delta = target - cumulative;
    const a = (node.weights[index] ?? 0) + delta;
    const b = (node.weights[index + 1] ?? 0) - delta;
    const min = total * MIN_FRACTION;
    if (a < min || b < min) {
      return node;
    }
    const weights = [...node.weights];
    weights[index] = a;
    weights[index + 1] = b;
    return { ...node, weights };
  });
}

function mapAtPath(
  node: LayoutNode,
  path: number[],
  fn: (n: LayoutNode) => LayoutNode,
): LayoutNode {
  if (path.length === 0) {
    return fn(node);
  }
  if (node.type !== "split") {
    return node;
  }
  const [head, ...rest] = path;
  return {
    ...node,
    children: node.children.map((child, i) => (i === head ? mapAtPath(child, rest, fn) : child)),
  };
}
