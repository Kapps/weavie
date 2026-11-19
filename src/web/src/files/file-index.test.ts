import { describe, expect, it } from "vitest";
import { FileIndex } from "./file-index";
import { buildPathTree, type PathTreeNode, visiblePathTreeRows } from "./path-tree";

function indexOf(root: string, paths: string[]) {
  const index = new FileIndex();
  index.setWorkspace(root, null, root.startsWith("/") ? "/" : "\\");
  const listing = index.newListing();
  for (const path of paths) listing.add(path);
  index.install(listing);
  return index;
}

const listed = (index: FileIndex): string[] => index.entries.map((entry) => entry.row.rel).sort();

// Every row of a tree with all of its folders open, as "depth kind key".
function unfolded(nodes: readonly PathTreeNode<string>[]): string[] {
  const folders = new Set<string>();
  const collect = (level: readonly PathTreeNode<string>[]): void => {
    for (const node of level) {
      if (node.kind === "directory") {
        folders.add(node.key);
        collect(node.children);
      }
    }
  };
  collect(nodes);
  return visiblePathTreeRows(nodes, folders, Number.POSITIVE_INFINITY).map(
    ({ node, depth }) => `${depth} ${node.kind} ${node.key}`,
  );
}

describe("FileIndex", () => {
  it("lists its tree one level at a time, matching a tree built from every file", () => {
    const index = indexOf("/repo", ["b.ts", "a.ts", "src/z.ts", "src/deep/k.ts", "docs/d.md"]);
    index.apply({ revision: 2, added: ["src/deep/er/m.ts", "new/n.ts"], removed: ["docs/d.md"] });
    index.apply({ revision: 3, added: [], removed: ["src/deep/er/m.ts", "src/deep/k.ts"] });
    const files = ["a.ts", "b.ts", "new/n.ts", "src/z.ts"];
    expect(unfolded(index.tree())).toEqual(
      unfolded(buildPathTree(files.map((path) => ({ path, value: `/repo/${path}` })))),
    );
    expect(index.tree().map((node) => node.name)).toEqual(["new", "src", "a.ts", "b.ts"]);
    expect(index.tree().find((node) => node.kind === "file")).toEqual({
      kind: "file",
      name: "a.ts",
      key: "a.ts",
      value: "/repo/a.ts",
    });
  });

  it("files a name holding a backslash where the tree and its reveal both look", () => {
    const paths = ["foo\\bar/file.ts", "plain/x.ts"];
    const index = indexOf("/repo", paths);
    expect(unfolded(index.tree())).toEqual(
      unfolded(buildPathTree(paths.map((path) => ({ path, value: `/repo/${path}` })))),
    );
    expect(index.find("foo\\bar/file.ts")?.abs).toBe("/repo/foo\\bar/file.ts");
    index.apply({ revision: 2, added: [], removed: ["foo\\bar/file.ts"] });
    expect(index.tree().map((node) => node.name)).toEqual(["plain"]);
  });

  it("joins paths with the host's separator, whatever characters the root holds", () => {
    const index = new FileIndex();
    index.setWorkspace("/tmp/a\\b", null, "/");
    const listing = index.newListing();
    listing.add("dir/file.ts");
    index.install(listing);
    expect(index.find("dir/file.ts")?.abs).toBe("/tmp/a\\b/dir/file.ts");
  });

  it("lists a path nested deeper than the stack", () => {
    const deep = `${Array.from({ length: 12_000 }, () => "d").join("/")}/file.ts`;
    const index = indexOf("/repo", [deep]);
    expect(index.tree().map((node) => node.name)).toEqual(["d"]);
    index.apply({ revision: 2, added: [], removed: [deep] });
    expect(index.tree()).toEqual([]);
  });

  it("keeps a folder's level until what sits directly inside it changes", () => {
    const index = indexOf("/repo", ["a.ts", "src/b.ts", "src/deep/c.ts"]);
    const root = index.tree();
    const src = root.find((node) => node.kind === "directory");
    if (src?.kind !== "directory") throw new Error("expected the src folder");
    const level = src.children;
    index.apply({ revision: 2, added: ["src/deep/d.ts"], removed: [] });
    expect(index.tree()).toBe(root);
    expect(src.children).toBe(level);
    index.apply({ revision: 3, added: ["src/e.ts"], removed: [] });
    expect(index.tree()).toBe(root);
    expect(src.children).not.toBe(level);
    expect(src.children.map((node) => node.name)).toEqual(["deep", "b.ts", "e.ts"]);
  });

  it("says why it has no files until its first list is installed", () => {
    const index = new FileIndex();
    index.setWorkspace("/repo", null, "/");
    expect(index.pending).toBe(true);
    expect(index.emptyText("No files")).toBe("Loading files…");
    index.setDownloading(true);
    expect(index.pending).toBe(false);
    expect(index.emptyText("No files")).toBe("Couldn't load files");
    index.setDownloading(false);
    expect(index.emptyText("No files")).toBe("Loading files…");
    index.install(index.newListing());
    expect(index.pending).toBe(false);
    expect(index.emptyText("No files")).toBe("No files");
  });

  it("keeps showing the list it holds when a later download fails", () => {
    const index = indexOf("/repo", ["a.ts"]);
    index.setDownloading(true);
    expect(index.emptyText("No files")).toBe("No files");
    expect(listed(index)).toEqual(["a.ts"]);
  });

  it("refuses a list before its workspace and a change before its list", () => {
    const index = new FileIndex();
    expect(() => index.newListing()).toThrow();
    index.setWorkspace("/repo", null, "/");
    expect(index.apply({ revision: 1, added: ["a.ts"], removed: [] })).toBe(false);
  });

  it("matches a rebuilt list after any sequence of changes", () => {
    const index = indexOf("/repo", ["a.ts", "src/b.ts", "src/c.ts", "docs/d.md"]);
    index.apply({ revision: 2, added: ["src/e.ts", "z.ts"], removed: ["a.ts", "src/c.ts"] });
    index.apply({ revision: 3, added: ["a.ts"], removed: ["z.ts", "docs/d.md"] });
    const rebuilt = indexOf("/repo", ["a.ts", "src/b.ts", "src/e.ts"]);
    expect(listed(index)).toEqual(listed(rebuilt));
    for (const rel of listed(rebuilt)) expect(index.find(rel)).toEqual(rebuilt.find(rel));
    expect(index.find("src/c.ts")).toBeUndefined();
  });

  it("bumps its version once per change that lists or drops a file", () => {
    const index = indexOf("/repo", ["a.ts"]);
    const before = index.version;
    index.apply({ revision: 2, added: [], removed: [] });
    expect(index.version).toBe(before);
    index.apply({ revision: 3, added: ["b.ts", "c.ts"], removed: ["a.ts"] });
    expect(index.version).toBe(before + 1);
  });

  it("leaves the list untouched by a change that does not fit it", () => {
    const index = indexOf("/repo", ["a.ts", "b.ts"]);
    const before = index.version;
    expect(index.apply({ revision: 2, added: [], removed: ["a.ts", "ghost.ts"] })).toBe(false);
    expect(index.apply({ revision: 2, added: ["b.ts"], removed: [] })).toBe(false);
    expect(index.apply({ revision: 2, added: ["c.ts", "c.ts"], removed: [] })).toBe(false);
    expect(listed(index)).toEqual(["a.ts", "b.ts"]);
    expect(index.version).toBe(before);
  });

  it("takes a change that drops a path and lists it again", () => {
    const index = indexOf("/repo", ["readme.md"]);
    expect(index.apply({ revision: 2, added: ["readme.md"], removed: ["readme.md"] })).toBe(true);
    expect(listed(index)).toEqual(["readme.md"]);
  });

  it("notifies workspace readers only when the workspace differs", () => {
    const index = new FileIndex();
    index.setWorkspace("/repo", "/home/me", "/");
    const first = index.root;
    index.setWorkspace("/repo", "/home/me", "/");
    expect(index.root).toBe(first);
    index.setWorkspace("/other", "/home/me", "/");
    expect(index.root).toBe("/other");
  });

  it("spells a file's path the way its host does", () => {
    expect(indexOf("/repo", ["src/a.ts"]).find("src/a.ts")).toEqual({
      abs: "/repo/src/a.ts",
      rel: "src/a.ts",
      leaf: "a.ts",
      dir: "src",
      leafStart: 4,
    });
    expect(indexOf("C:\\Repo", ["src/a.ts"]).find("src/a.ts")?.abs).toBe("c:\\Repo\\src\\a.ts");
  });

  it("finds a loosely spelled path only when one file answers to it", () => {
    const index = indexOf("/repo", ["Dir/Foo.ts", "two.ts", "Two.ts"]);
    expect(index.findLoose("dir\\FOO.ts")?.rel).toBe("Dir/Foo.ts");
    expect(index.findLoose("TWO.ts")).toBeUndefined();
    index.apply({ revision: 2, added: [], removed: ["Two.ts"] });
    expect(index.findLoose("TWO.ts")?.rel).toBe("two.ts");
  });
});
