import { describe, expect, it } from "vitest";
import { looksLikePath, parsePathQuery } from "./path-query";

const posix = { root: "/ws/repo", home: "/home/deb" };

describe("looksLikePath", () => {
  it("recognizes the shapes a user means as a path", () => {
    for (const q of ["/a", "~/a", "~", "C:\\a", "c:/a", "\\\\srv\\share\\a", "./a", "../a"]) {
      expect(looksLikePath(q), q).toBe(true);
    }
  });

  it("leaves ordinary fuzzy queries alone", () => {
    // The regression that would break Go to File: these must stay fuzzy file/command/symbol queries.
    for (const q of ["src/foo", "Omnibar", ">cmd", "@sym", "#sym", "", "a.ts", "..dots"]) {
      expect(looksLikePath(q), q).toBe(false);
    }
  });
});

describe("parsePathQuery", () => {
  it("returns null for a non-path query", () => {
    expect(parsePathQuery("src/foo", posix)).toBeNull();
  });

  it("splits an absolute path into the directory to list and the partial leaf", () => {
    expect(parsePathQuery("/a/b/par", posix)).toEqual({
      absolute: "/a/b/par",
      dir: "/a/b",
      leaf: "par",
    });
  });

  it("treats a trailing separator as asking for the whole directory", () => {
    expect(parsePathQuery("/a/b/", posix)).toEqual({ absolute: "/a/b/", dir: "/a/b", leaf: "" });
  });

  it("lists the filesystem root for a top-level path", () => {
    expect(parsePathQuery("/et", posix)).toEqual({ absolute: "/et", dir: "/", leaf: "et" });
  });

  it("expands ~ against the host's home, not the browser's", () => {
    expect(parsePathQuery("~/notes.md", posix)?.absolute).toBe("/home/deb/notes.md");
    // Unknown home: refuse rather than resolve it against the worktree and name a directory nobody meant.
    expect(parsePathQuery("~/notes.md", { root: "/ws", home: null })).toBeNull();
  });

  it("resolves ../ against the root, which is the sibling-repo case", () => {
    expect(parsePathQuery("../other/src", posix)?.absolute).toBe("/ws/other/src");
    expect(parsePathQuery("./src", posix)?.absolute).toBe("/ws/repo/src");
  });

  it("follows the root's separator flavor on Windows", () => {
    const win = { root: "C:\\ws\\repo", home: "C:\\Users\\deb" };
    expect(parsePathQuery("../other", win)?.absolute).toBe("C:\\ws\\other");
    expect(parsePathQuery("C:\\a\\b", win)).toEqual({
      absolute: "C:\\a\\b",
      dir: "C:\\a",
      leaf: "b",
    });
  });
});

describe("parsePathQuery filesystem roots", () => {
  it("lists the drive root for a bare Windows drive, not the drive's current directory", () => {
    const win = { root: "C:\\ws", home: null };
    expect(parsePathQuery("C:\\a", win)).toEqual({ absolute: "C:\\a", dir: "C:\\", leaf: "a" });
    expect(parsePathQuery("C:/a", win)).toEqual({ absolute: "C:/a", dir: "C:/", leaf: "a" });
  });

  it("stops at the filesystem root instead of popping past it", () => {
    expect(parsePathQuery("../../..", { root: "/ws/repo", home: null })?.absolute).toBe("/");
    expect(parsePathQuery("../..", { root: "C:\\ws\\repo", home: null })?.absolute).toBe("C:\\");
  });

  it("refuses ~ when the host reports no home directory", () => {
    expect(parsePathQuery("~/notes.md", { root: "/ws", home: null })).toBeNull();
  });
});
