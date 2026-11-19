import { describe, expect, it } from "vitest";
import { FileIndex } from "./file-index";

function indexOf(root: string, paths: string[]) {
  const index = new FileIndex();
  index.setWorkspace(root, null);
  const listing = index.newListing();
  for (const path of paths) listing.add(path);
  index.install(listing);
  return index;
}

const listed = (index: FileIndex): string[] => index.entries.map((entry) => entry.row.rel).sort();

describe("FileIndex", () => {
  it("is pending until its first list is installed", () => {
    const index = new FileIndex();
    index.setWorkspace("/repo", null);
    expect(index.pending).toBe(true);
    index.install(index.newListing());
    expect(index.pending).toBe(false);
  });

  it("refuses a list before its workspace and a change before its list", () => {
    const index = new FileIndex();
    expect(() => index.newListing()).toThrow();
    index.setWorkspace("/repo", null);
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
    index.setWorkspace("/repo", "/home/me");
    const first = index.root;
    index.setWorkspace("/repo", "/home/me");
    expect(index.root).toBe(first);
    index.setWorkspace("/other", "/home/me");
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
