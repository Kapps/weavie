import { describe, expect, it } from "vitest";
import {
  buildPathTree,
  compactPathTree,
  pathAncestorKeys,
  pathTreeDirectoryKeys,
  visiblePathTreeRows,
} from "./path-tree";

describe("path tree", () => {
  it("groups mixed separators and sorts directories before files", () => {
    const tree = buildPathTree([
      { path: "root.txt", value: 1 },
      { path: "src\\z.ts", value: 2 },
      { path: "docs/readme.md", value: 3 },
      { path: "src/a.ts", value: 4 },
    ]);

    expect(tree).toEqual([
      {
        kind: "directory",
        name: "docs",
        key: "docs",
        children: [{ kind: "file", name: "readme.md", key: "docs/readme.md", value: 3 }],
      },
      {
        kind: "directory",
        name: "src",
        key: "src",
        children: [
          { kind: "file", name: "a.ts", key: "src/a.ts", value: 4 },
          { kind: "file", name: "z.ts", key: "src/z.ts", value: 2 },
        ],
      },
      { kind: "file", name: "root.txt", key: "root.txt", value: 1 },
    ]);
  });

  it("flattens expanded branches to a fixed bound", () => {
    const tree = buildPathTree([
      { path: "src/deep/a.ts", value: "a" },
      { path: "src/b.ts", value: "b" },
    ]);

    expect(pathTreeDirectoryKeys(tree)).toEqual(["src", "src/deep"]);
    expect(pathAncestorKeys("src\\deep/a.ts")).toEqual(["src", "src/deep"]);
    expect(
      visiblePathTreeRows(tree, new Set(["src", "src/deep"]), 3).map((row) => [
        row.node.key,
        row.depth,
      ]),
    ).toEqual([
      ["src", 0],
      ["src/deep", 1],
      ["src/deep/a.ts", 2],
    ]);
    expect(visiblePathTreeRows(tree, new Set(), Number.POSITIVE_INFINITY)).toHaveLength(1);
  });

  it("merges single-folder chains into one row keyed by the deepest folder", () => {
    const tree = compactPathTree(
      buildPathTree([
        { path: "src/components/forms/a.tsx", value: "a" },
        { path: "src/app/b.ts", value: "b" },
      ]),
    );

    expect(
      visiblePathTreeRows(tree, new Set(pathTreeDirectoryKeys(tree)), Number.POSITIVE_INFINITY).map(
        (row) => [row.node.name, row.node.key, row.depth],
      ),
    ).toEqual([
      ["src", "src", 0],
      ["app", "src/app", 1],
      ["b.ts", "src/app/b.ts", 2],
      ["components/forms", "src/components/forms", 1],
      ["a.tsx", "src/components/forms/a.tsx", 2],
    ]);
  });
});
