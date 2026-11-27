import type * as monaco from "monaco-editor";
import { describe, expect, it, vi } from "vitest";

vi.mock("./session-uri", () => ({
  SESSION_FILE_SCHEME: "weavie-file",
  sessionUriHostPath: () => "",
}));

const { quoteOf, selectionQuote } = await import("./selection-excerpt");

const model = (text: string) => {
  const lines = text.split("\n");
  return {
    getLineContent: (line: number) => lines[line - 1]!,
    getLineMaxColumn: (line: number) => lines[line - 1]!.length + 1,
  } as unknown as monaco.editor.ITextModel;
};

describe("anchor quotes", () => {
  it("quote a selection's first line with text, as long as an anchor allows", () => {
    expect(quoteOf("\n  first line  \nsecond")).toBe("first line");
    expect(quoteOf("x".repeat(300))).toHaveLength(200);
  });

  it("cut a code selection to its last line with text, which they quote", () => {
    const code = model("class Fields {\n\n  a = 1;");
    expect(
      selectionQuote(code, { startLineNumber: 1, startColumn: 1, endLineNumber: 3, endColumn: 1 }),
    ).toEqual({
      range: { startLineNumber: 1, startColumn: 1, endLineNumber: 1, endColumn: 15 },
      quote: "class Fields {",
    });
    expect(
      selectionQuote(code, { startLineNumber: 3, startColumn: 3, endLineNumber: 3, endColumn: 4 }),
    ).toMatchObject({ quote: "a" });
    expect(
      selectionQuote(code, { startLineNumber: 2, startColumn: 1, endLineNumber: 2, endColumn: 1 }),
    ).toBeNull();
  });
});
