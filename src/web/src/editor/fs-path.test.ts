import { describe, expect, it } from "vitest";
import { canonicalFsPath, normalizePath, samePath } from "./fs-path";

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
