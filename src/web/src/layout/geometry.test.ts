import { describe, expect, it } from "vitest";
import { computeRects, computeSplitters, paneOrder, setBoundary } from "./geometry";
import type { LayoutNode } from "./types";

// The seeded default: a 40/60 row split whose left child is a 50/50 column of agent + shell.
function tree(): LayoutNode {
  return {
    type: "split",
    dir: "row",
    weights: [0.4, 0.6],
    children: [
      {
        type: "split",
        dir: "column",
        weights: [0.5, 0.5],
        children: [
          { type: "pane", id: "p_claude", kind: "terminal:claude" },
          { type: "pane", id: "p_shell", kind: "terminal:shell" },
        ],
      },
      { type: "pane", id: "p_editor", kind: "editor" },
    ],
  };
}

describe("paneOrder", () => {
  it("lists pane kinds in DFS (Ctrl+1..9) order", () => {
    expect(paneOrder(tree())).toEqual(["terminal:claude", "terminal:shell", "editor"]);
  });
});

describe("computeRects", () => {
  it("turns weights into percentage rectangles", () => {
    const rects = computeRects(tree());
    expect(rects.get("editor")).toEqual({ x: 40, y: 0, w: 60, h: 100 });
    expect(rects.get("terminal:claude")).toEqual({ x: 0, y: 0, w: 40, h: 50 });
    expect(rects.get("terminal:shell")).toEqual({ x: 0, y: 50, w: 40, h: 50 });
  });

  it("a lone pane fills the viewport", () => {
    expect(computeRects({ type: "pane", id: "p", kind: "editor" }).get("editor")).toEqual({
      x: 0,
      y: 0,
      w: 100,
      h: 100,
    });
  });
});

describe("computeSplitters", () => {
  it("emits one handle per internal boundary with drag geometry", () => {
    const handles = computeSplitters(tree());
    expect(handles).toHaveLength(2);

    const row = handles.find((h) => h.dir === "row" && h.path.length === 0);
    expect(row).toMatchObject({ index: 0, x: 40, y: 0, cross: 100, axisStart: 0, axisSize: 100 });

    const col = handles.find((h) => h.dir === "column");
    expect(col).toMatchObject({
      path: [0],
      index: 0,
      x: 0,
      y: 50,
      cross: 40,
      axisStart: 0,
      axisSize: 100,
    });
  });
});

describe("setBoundary", () => {
  it("resizes visible neighbours across a hidden leaf without changing its saved weight", () => {
    const root: LayoutNode = {
      type: "split",
      dir: "row",
      weights: [0.2, 0.3, 0.5],
      children: [
        { type: "pane", id: "files", kind: "files" },
        { type: "pane", id: "search", kind: "search", hidden: true },
        { type: "pane", id: "editor", kind: "editor" },
      ],
    };
    expect(computeSplitters(root)).toHaveLength(1);
    const next = setBoundary(root, [], 0, 0.5) as Extract<LayoutNode, { type: "split" }>;
    expect(next.weights).toEqual([0.35, 0.3, 0.35000000000000003]);
    expect(computeRects(next).has("search")).toBe(false);
    expect(computeRects(next).get("files")?.w).toBeCloseTo(50);
  });

  it("collapses an entirely hidden tool column without losing nested splitter paths", () => {
    const primary = tree();
    const root: LayoutNode = {
      type: "split",
      dir: "row",
      weights: [0.2, 0.8],
      children: [
        {
          type: "split",
          dir: "column",
          weights: [0.6, 0.4],
          children: [
            { type: "pane", id: "files", kind: "files", hidden: true },
            { type: "pane", id: "search", kind: "search", hidden: true },
          ],
        },
        primary,
      ],
    };
    expect(computeRects(root)).toEqual(computeRects(primary));
    expect(computeSplitters(root).map((splitter) => splitter.path)).toEqual([[1, 0], [1]]);
    const resized = setBoundary(root, [1], 0, 0.3);
    expect(computeRects(resized).get("editor")?.w).toBeCloseTo(70);
    expect((resized as Extract<LayoutNode, { type: "split" }>).children[0]).toBe(root.children[0]);
  });
  it("moves a split's boundary to the requested fraction, preserving the weight total", () => {
    const next = setBoundary(tree(), [], 0, 0.3) as Extract<LayoutNode, { type: "split" }>;
    expect(next.weights[0]).toBeCloseTo(0.3);
    expect(next.weights[1]).toBeCloseTo(0.7);
  });

  it("returns a new tree without mutating the original", () => {
    const original = tree();
    setBoundary(original, [], 0, 0.3);
    expect((original as Extract<LayoutNode, { type: "split" }>).weights).toEqual([0.4, 0.6]);
  });

  it("resolves a nested path", () => {
    const next = setBoundary(tree(), [0], 0, 0.8);
    const col = (next as Extract<LayoutNode, { type: "split" }>).children[0] as Extract<
      LayoutNode,
      { type: "split" }
    >;
    expect(col.weights[0]).toBeCloseTo(0.8);
    expect(col.weights[1]).toBeCloseTo(0.2);
  });

  it("refuses to shrink a neighbour below the 5% minimum", () => {
    const next = setBoundary(tree(), [], 0, 0.01) as Extract<LayoutNode, { type: "split" }>;
    expect(next.weights).toEqual([0.4, 0.6]);
  });
});
