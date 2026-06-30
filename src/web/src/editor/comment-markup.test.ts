import { describe, expect, it } from "vitest";
import { commentSyntaxFor, parseCommentLines, scanCommentBlocks } from "./comment-markup";

const DEFAULT = commentSyntaxFor("plaintext-unknown");

describe("commentSyntaxFor", () => {
  it("falls back to the C-family default for an unknown language", () => {
    expect(commentSyntaxFor("nope")).toEqual({ line: ["//"], block: ["/*", "*/"] });
  });

  it("uses #-comments for python and no block syntax", () => {
    expect(commentSyntaxFor("python")).toEqual({ line: ["#"] });
  });

  it("lists doc prefixes first so /// wins over // for C#", () => {
    expect(commentSyntaxFor("csharp").line).toEqual(["///", "//"]);
    expect(commentSyntaxFor("csharp").xmlDoc).toBe(true);
  });
});

describe("scanCommentBlocks — line comments", () => {
  it("groups a consecutive run into one block and strips the marker + one space", () => {
    const blocks = scanCommentBlocks(["// alpha", "// beta", "const x = 1;"], DEFAULT);
    expect(blocks).toHaveLength(1);
    expect(blocks[0]).toMatchObject({
      startLine: 1,
      endLine: 2,
      doc: false,
      content: ["alpha", "beta"],
    });
  });

  it("marks a run doc only when every line uses a doc prefix", () => {
    // The /// doc prefix only exists in a syntax that lists it (C#/Rust/…), not the C-family default.
    const cs = commentSyntaxFor("csharp");
    expect(scanCommentBlocks(["/// docs"], cs)[0]).toMatchObject({ doc: true, content: ["docs"] });
    expect(scanCommentBlocks(["/// a", "// b"], cs)[0]).toMatchObject({
      doc: false,
      content: ["a", "b"],
    });
  });

  it("skips a trailing comment after code", () => {
    expect(scanCommentBlocks(["const x = 1; // tail"], DEFAULT)).toEqual([]);
  });
});

describe("scanCommentBlocks — block comments", () => {
  it("captures a multi-line block, stripping delimiters and the * gutter", () => {
    const blocks = scanCommentBlocks(["/* one", " * two", " */"], DEFAULT);
    expect(blocks).toHaveLength(1);
    expect(blocks[0]).toMatchObject({ startLine: 1, endLine: 3, doc: false });
    expect(blocks[0]?.content.slice(0, 2)).toEqual(["one", "two"]);
  });

  it("flags a /** doc block", () => {
    const blocks = scanCommentBlocks(["/** docs", " * more", " */"], DEFAULT);
    expect(blocks[0]?.doc).toBe(true);
    expect(blocks[0]?.content.slice(0, 2)).toEqual(["docs", "more"]);
  });

  it("treats an unterminated block as running to EOF", () => {
    const blocks = scanCommentBlocks(["/* open", "still going"], DEFAULT);
    expect(blocks[0]).toMatchObject({ startLine: 1, endLine: 2 });
  });

  it("recognises a JSX {/* … */} expression-container comment in tsx", () => {
    const blocks = scanCommentBlocks(["{/* hi */}"], commentSyntaxFor("typescriptreact"));
    expect(blocks[0]).toMatchObject({ startLine: 1, endLine: 1, doc: false, content: ["hi"] });
  });
});

describe("parseCommentLines — inline backticks", () => {
  it("turns a `code` span into a code chip", () => {
    expect(parseCommentLines(["use `foo` here"], false)).toEqual([
      [{ text: "use " }, { code: "foo" }, { text: " here" }],
    ]);
  });

  it("keeps a literal backtick for an unterminated span", () => {
    expect(parseCommentLines(["a `b"], false)).toEqual([[{ text: "a `b" }]]);
  });

  it("represents an empty line as a single empty text run", () => {
    expect(parseCommentLines([""], false)).toEqual([[{ text: "" }]]);
  });
});

describe("parseCommentLines — Markdown emphasis", () => {
  it("marks *italic* / _italic_ as em runs", () => {
    expect(parseCommentLines(["a *b* and _c_"], false)).toEqual([
      [{ text: "a " }, { text: "b", em: true }, { text: " and " }, { text: "c", em: true }],
    ]);
  });

  it("marks **bold** / __bold__ as strong runs", () => {
    expect(parseCommentLines(["**b** __c__"], false)).toEqual([
      [{ text: "b", strong: true }, { text: " " }, { text: "c", strong: true }],
    ]);
  });

  it("marks ~~struck~~ text as a strike run", () => {
    expect(parseCommentLines(["~~gone~~"], false)).toEqual([[{ text: "gone", strike: true }]]);
  });

  it("combines nested emphasis (***bold italic***)", () => {
    expect(parseCommentLines(["***x***"], false)).toEqual([
      [{ text: "x", strong: true, em: true }],
    ]);
  });

  it("leaves an intraword underscore (snake_case) as plain text", () => {
    expect(parseCommentLines(["call my_func_name now"], false)).toEqual([
      [{ text: "call my_func_name now" }],
    ]);
  });

  it("does not emphasise an asterisk used as multiplication", () => {
    expect(parseCommentLines(["n = a * b * c"], false)).toEqual([[{ text: "n = a * b * c" }]]);
  });
});

describe("parseCommentLines — XML doc", () => {
  it("drops structural wrappers and lifts <c> to a code chip", () => {
    const runs = parseCommentLines(["<summary>Does <c>X</c></summary>"], true)[0]!;
    expect(runs).toContainEqual({ code: "X" });
    expect(runs.some((r) => "text" in r && r.text.includes("Does"))).toBe(true);
  });

  it("reduces a <see cref> to its last dotted segment", () => {
    const runs = parseCommentLines(['<see cref="System.String"/>'], true)[0]!;
    expect(runs).toContainEqual({ code: "String" });
  });

  it("turns a <param> into a labelled line with a code chip for the name", () => {
    const runs = parseCommentLines(['<param name="count">the total</param>'], true)[0]!;
    expect(runs).toContainEqual({ code: "count" });
    expect(runs.some((r) => "text" in r && r.text.includes("the total"))).toBe(true);
  });
});
