import { describe, expect, test } from "vitest";
import { blockSource, buildUpdateOp } from "./notion-edit";

describe("blockSource", () => {
  test("slices tabs + display + raw attrs byte-exactly", () => {
    const line = '\t\tShip the MVP by **Friday**. {color="blue_bg"}';
    const source = blockSource(`before\n${line}\nafter`, 1);
    expect(source).toEqual({
      tabs: "\t\t",
      display: "Ship the MVP by **Friday**.",
      attrsRaw: ' {color="blue_bg"}',
    });
    expect(source.tabs + source.display + source.attrsRaw).toBe(line);
  });

  test("a literal trailing brace that isn't a Notion attr stays in the display", () => {
    const source = blockSource("see {note}", 0);
    expect(source).toEqual({ tabs: "", display: "see {note}", attrsRaw: "" });
  });

  test("a plain line is all display", () => {
    expect(blockSource("Hello world", 0)).toEqual({
      tabs: "",
      display: "Hello world",
      attrsRaw: "",
    });
  });

  test("throws on a line index outside the document", () => {
    expect(() => blockSource("one line", 3)).toThrow(/no line 3/i);
  });
});

describe("buildUpdateOp", () => {
  test("a unique middle line ships newline-anchored, alone", () => {
    const markdown = ["First", "Second", "Third"].join("\n");
    expect(buildUpdateOp(markdown, 1, "Second, edited")).toEqual({
      ok: true,
      oldStr: "\nSecond\n",
      newStr: "\nSecond, edited\n",
    });
  });

  test("first and last lines carry no boundary newline on the document edge", () => {
    const markdown = ["First", "Second"].join("\n");
    expect(buildUpdateOp(markdown, 0, "First!")).toEqual({
      ok: true,
      oldStr: "First\n",
      newStr: "First!\n",
    });
    expect(buildUpdateOp(markdown, 1, "Second!")).toEqual({
      ok: true,
      oldStr: "\nSecond",
      newStr: "\nSecond!",
    });
  });

  test("tabs and trailing attrs are re-attached invisibly — formatting survives the edit", () => {
    const markdown = [
      '<callout icon="💡" color="green_bg">',
      '\tOld text {color="blue"}',
      "</callout>",
    ].join("\n");
    const op = buildUpdateOp(markdown, 1, "New text");
    expect(op).toEqual({
      ok: true,
      oldStr: '\n\tOld text {color="blue"}\n',
      newStr: '\n\tNew text {color="blue"}\n',
    });
  });

  test("a duplicated line grows neighbor context until it matches exactly once", () => {
    const markdown = ["Intro", "Yes", "Middle", "Yes", "Outro"].join("\n");
    const op = buildUpdateOp(markdown, 3, "Yes indeed");
    expect(op.ok).toBe(true);
    if (op.ok) {
      // The bare "\nYes\n" matches both items; context disambiguates, and only the target line changes.
      expect(op.oldStr).toBe("\nMiddle\nYes\n");
      expect(op.newStr).toBe("\nMiddle\nYes indeed\n");
      expect(countOf(markdown, op.oldStr)).toBe(1);
    }
  });

  test("a duplicated REGION keeps growing until unique", () => {
    const markdown = ["A", "B", "X", "A", "B", "Y"].join("\n");
    const op = buildUpdateOp(markdown, 4, "B2");
    // "\nB\n" matches both B lines; growing upward to "\nA\nB\n" is unique (the first A\nB starts the document,
    // so the leading newline anchor rules it out).
    expect(op).toEqual({ ok: true, oldStr: "\nA\nB\n", newStr: "\nA\nB2\n" });
    if (op.ok) {
      expect(countOf(markdown, op.oldStr)).toBe(1);
    }
  });

  test("identical adjacent lines at the document edges resolve through the boundary anchors", () => {
    const markdown = ["Same", "Same"].join("\n");
    // The document edge is itself an anchor: "Same\n" only fits the first line, "\nSame" only the last.
    expect(buildUpdateOp(markdown, 0, "Same but first")).toEqual({
      ok: true,
      oldStr: "Same\n",
      newStr: "Same but first\n",
    });
    expect(buildUpdateOp(markdown, 1, "Same but second")).toEqual({
      ok: true,
      oldStr: "\nSame",
      newStr: "\nSame but second",
    });
  });

  test("refuses an emptied draft — deletion is out of scope", () => {
    expect(buildUpdateOp("Hello", 0, "   ")).toEqual({
      ok: false,
      reason: expect.stringContaining("delete it in Notion"),
    });
  });

  test("refuses a multi-line draft — one block per edit", () => {
    expect(buildUpdateOp("Hello", 0, "two\nlines")).toEqual({
      ok: false,
      reason: expect.stringContaining("One block at a time"),
    });
  });
});

function countOf(haystack: string, needle: string): number {
  let count = 0;
  let index = haystack.indexOf(needle);
  while (index !== -1) {
    count++;
    index = haystack.indexOf(needle, index + 1);
  }
  return count;
}
