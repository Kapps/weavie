import { describe, expect, it } from "vitest";
import { findContentLinks, parseFileReference } from "./content-links";

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
});

describe("parseFileReference", () => {
  it("keeps the Windows drive colon and discards the optional column", () => {
    expect(parseFileReference("C:\\src\\main.ts:17:3")).toEqual({
      path: "C:\\src\\main.ts",
      line: 17,
    });
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
