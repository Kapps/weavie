import { describe, expect, test } from "vitest";
import { codeBlockCopyText } from "./code-block-copy";

describe("code block copy", () => {
  test("drops the block boundary's trailing newline", () => {
    expect(codeBlockCopyText("dotnet run tools/display-refresh.cs\n")).toBe(
      "dotnet run tools/display-refresh.cs",
    );
  });

  test("keeps a selection that has no trailing newline", () => {
    expect(codeBlockCopyText("git status")).toBeNull();
  });

  test("keeps every newline between selected lines", () => {
    expect(codeBlockCopyText("first\nsecond\nthird\n")).toBe("first\nsecond\nthird");
  });

  test("keeps a genuinely selected blank final line", () => {
    expect(codeBlockCopyText("git status\n\n")).toBeNull();
  });
});
