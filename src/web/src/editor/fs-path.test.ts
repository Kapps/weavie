import { describe, expect, it } from "vitest";
import {
  basename,
  canonicalFsPath,
  normalizePath,
  repoRelativePath,
  samePath,
  uriHostPath,
} from "./fs-path";

describe("canonicalFsPath", () => {
  it("lowercases an uppercase drive letter, leaving the rest untouched", () => {
    expect(canonicalFsPath("C:/Users/Foo.ts")).toBe("c:/Users/Foo.ts");
    expect(canonicalFsPath("D:\\src\\Bar.ts")).toBe("d:\\src\\Bar.ts");
  });

  it("leaves an already-lowercase drive and non-drive paths unchanged", () => {
    expect(canonicalFsPath("c:/users/foo.ts")).toBe("c:/users/foo.ts");
    expect(canonicalFsPath("/usr/local/foo.ts")).toBe("/usr/local/foo.ts");
  });

  it("only touches the leading drive colon, not a later colon", () => {
    // A line:col-looking suffix has its own colon that must survive.
    expect(canonicalFsPath("C:/a/b:42")).toBe("c:/a/b:42");
  });
});

describe("uriHostPath", () => {
  it("passes a POSIX path through untouched, whatever the client OS", () => {
    // The regression: .fsPath on a Windows browser turns this into \home\user\a.ts, which a Linux host
    // can't resolve. uriHostPath must be client-platform-independent.
    expect(uriHostPath({ authority: "", path: "/home/user/a.ts" })).toBe("/home/user/a.ts");
  });

  it("renders a drive path without the URI's leading slash, lowercasing the drive", () => {
    expect(uriHostPath({ authority: "", path: "/C:/Users/foo.ts" })).toBe("c:/Users/foo.ts");
    expect(uriHostPath({ authority: "", path: "/c:/users/foo.ts" })).toBe("c:/users/foo.ts");
  });

  it("renders a drive root as c:/ (a bare c: would be drive-relative on Windows)", () => {
    expect(uriHostPath({ authority: "", path: "/C:" })).toBe("c:/");
  });

  it("keeps a UNC authority", () => {
    expect(uriHostPath({ authority: "server", path: "/share/foo.ts" })).toBe(
      "//server/share/foo.ts",
    );
  });

  it("preserves a literal backslash in a POSIX filename", () => {
    expect(uriHostPath({ authority: "", path: "/home/user/weird\\name.ts" })).toBe(
      "/home/user/weird\\name.ts",
    );
  });
});

describe("normalizePath", () => {
  it("unifies separators and folds case", () => {
    expect(normalizePath("C:\\Src\\Foo.TS")).toBe("c:/src/foo.ts");
  });

  it("maps a WSL /mnt/<drive>/ mount onto <drive>:", () => {
    expect(normalizePath("/mnt/c/Users/foo")).toBe("c:/users/foo");
  });

  it("does not mistake a real directory like /mnt/claude for a drive mount", () => {
    // The lookahead requires the segment after /mnt/ to be a single char, so multi-letter names are kept.
    expect(normalizePath("/mnt/claude/work")).toBe("/mnt/claude/work");
  });

  it("drops a trailing slash", () => {
    expect(normalizePath("C:/foo/")).toBe("c:/foo");
    expect(normalizePath("C:\\foo\\")).toBe("c:/foo");
  });
});

describe("basename", () => {
  it("returns the final segment for posix and windows paths", () => {
    expect(basename("/home/user/proj/app.ts")).toBe("app.ts");
    expect(basename("C:\\src\\Foo.cs")).toBe("Foo.cs");
  });

  it("ignores a trailing slash and falls back to the input when there's no segment", () => {
    expect(basename("/home/user/proj/")).toBe("proj");
    expect(basename("app.ts")).toBe("app.ts");
  });
});

describe("repoRelativePath", () => {
  it("strips the root prefix, keeping the original separators and casing", () => {
    expect(repoRelativePath("/home/user/proj", "/home/user/proj/src/App.ts")).toBe("src/App.ts");
    expect(repoRelativePath("C:\\Proj", "C:\\Proj\\src\\App.cs")).toBe("src\\App.cs");
  });

  it("matches the prefix case- and separator-insensitively", () => {
    expect(repoRelativePath("c:/proj", "C:\\Proj\\src\\App.cs")).toBe("src\\App.cs");
  });

  it("tolerates a trailing slash on the root", () => {
    expect(repoRelativePath("/home/user/proj/", "/home/user/proj/a.ts")).toBe("a.ts");
  });

  it("returns the file name when the path is the root itself", () => {
    expect(repoRelativePath("/home/user/proj", "/home/user/proj")).toBe("proj");
  });

  it("returns the untouched path when it lies outside the root", () => {
    expect(repoRelativePath("/home/user/proj", "/tmp/scratch.ts")).toBe("/tmp/scratch.ts");
  });
});

describe("samePath", () => {
  it("treats drive-letter case, separators, and filename case as the same file", () => {
    expect(samePath("C:\\Foo\\Bar.ts", "c:/foo/bar.ts")).toBe(true);
  });

  it("folds the WSL mount onto the Windows drive spelling", () => {
    expect(samePath("/mnt/c/proj/app.ts", "C:\\proj\\app.ts")).toBe(true);
  });

  it("distinguishes genuinely different files", () => {
    expect(samePath("c:/foo/a.ts", "c:/foo/b.ts")).toBe(false);
  });
});
