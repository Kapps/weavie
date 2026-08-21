import { describe, expect, it } from "vitest";
import { findContentLinks, isFileLineReference, parseFileReference } from "./content-links";

describe("findContentLinks", () => {
  it("finds web, file, and forge references without overlapping them", () => {
    expect(
      findContentLinks("See https://example.com/app.js:10, src/main.ts:42, and #18.", true),
    ).toEqual([
      { start: 4, end: 33, text: "https://example.com/app.js:10", kind: "url" },
      { start: 35, end: 49, text: "src/main.ts:42", kind: "file" },
      { start: 55, end: 58, text: "#18", kind: "ref" },
    ]);
  });

  it("omits forge references when no forge origin is available", () => {
    expect(findContentLinks("See #18 and src/main.ts", false)).toEqual([
      { start: 12, end: 23, text: "src/main.ts", kind: "file" },
    ]);
  });

  it("finds file URIs", () => {
    expect(findContentLinks("Open file:///home/user/a%20b.ts#12.", false)).toEqual([
      { start: 5, end: 34, text: "file:///home/user/a%20b.ts#12", kind: "file" },
    ]);
  });

  it("links a line range as one reference, not just its first line", () => {
    expect(findContentLinks("see processor.go:46-96 and a.ts:4:2-9:8", false)).toEqual([
      { start: 4, end: 22, text: "processor.go:46-96", kind: "file" },
      { start: 27, end: 39, text: "a.ts:4:2-9:8", kind: "file" },
    ]);
  });

  it("links a bare path whose filename contains @ (e.g. Playwright recordings)", () => {
    const path = "src/web/e2e/.recordings/page@883bef3dba4a5a81116faeb690fc011f.webm";
    expect(findContentLinks(`Recording ${path} saved`, false)).toEqual([
      { start: 10, end: 10 + path.length, text: path, kind: "file" },
    ]);
  });
});

describe("isFileLineReference", () => {
  it("recognizes the path:line shapes a URI parser would read as a scheme", () => {
    expect(isFileLineReference("hello.ts:42")).toBe(true);
    expect(isFileLineReference("processor.go:46-96")).toBe(true);
    expect(isFileLineReference("src/app.ts:12:4")).toBe(true);
  });

  it("rejects anything that is not exactly a reference", () => {
    expect(isFileLineReference("hello.ts")).toBe(false);
    expect(isFileLineReference("mailto:someone@example.com")).toBe(false);
    expect(isFileLineReference("https://example.com/a.ts:4")).toBe(false);
    expect(isFileLineReference("see hello.ts:42 now")).toBe(false);
  });
});

describe("parseFileReference", () => {
  it("keeps the Windows drive colon and discards the optional column", () => {
    expect(parseFileReference("C:\\src\\main.ts:17:3")).toEqual({
      path: "C:\\src\\main.ts",
      line: 17,
    });
  });

  it("reveals the first line of a range", () => {
    expect(parseFileReference("processor.go:46-96")).toEqual({ path: "processor.go", line: 46 });
    expect(parseFileReference("a.ts:4:2-9:8")).toEqual({ path: "a.ts", line: 4 });
  });

  it("defaults a bare path to its first line", () => {
    expect(parseFileReference("src/main.ts")).toEqual({ path: "src/main.ts", line: 1 });
  });

  it("decodes file URIs and reads their line fragment", () => {
    expect(parseFileReference("file:///home/user/a%20b.ts#12")).toEqual({
      path: "/home/user/a b.ts",
      line: 12,
    });
  });
});
