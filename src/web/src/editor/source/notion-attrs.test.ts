import { describe, expect, test } from "vitest";
import { notionColorClass, parseTagAttrs, parseTrailingAttrs } from "./notion-attrs";

describe("notionColorClass", () => {
  test("text colors map to wv-color-*", () => {
    expect(notionColorClass("blue")).toBe("wv-color-blue");
    expect(notionColorClass("RED")).toBe("wv-color-red");
  });
  test("background colors map to wv-bg-*", () => {
    expect(notionColorClass("blue_bg")).toBe("wv-bg-blue");
  });
  test("unknown colors are dropped", () => {
    expect(notionColorClass("chartreuse")).toBeNull();
    expect(notionColorClass("teal_bg")).toBeNull();
  });
});

describe("parseTrailingAttrs", () => {
  test("splits a trailing color attribute off the text", () => {
    expect(parseTrailingAttrs('Heading {color="blue"}')).toEqual({
      rest: "Heading",
      color: "blue",
      toggle: false,
    });
  });
  test("reads toggle and color together", () => {
    expect(parseTrailingAttrs('H {toggle="true" color="red"}')).toEqual({
      rest: "H",
      color: "red",
      toggle: true,
    });
  });
  test("leaves plain text untouched", () => {
    expect(parseTrailingAttrs("plain text")).toEqual({
      rest: "plain text",
      color: null,
      toggle: false,
    });
  });
  test("only consumes a brace block at the very end", () => {
    expect(parseTrailingAttrs("use {x} inline")).toEqual({
      rest: "use {x} inline",
      color: null,
      toggle: false,
    });
  });
  test("a trailing brace without a Notion attribute is left as text", () => {
    expect(parseTrailingAttrs("see {note}")).toEqual({
      rest: "see {note}",
      color: null,
      toggle: false,
    });
  });
});

describe("parseTagAttrs", () => {
  test("reads every key=value pair off a tag", () => {
    expect(parseTagAttrs('<callout icon="💡" color="green_bg">')).toEqual({
      icon: "💡",
      color: "green_bg",
    });
  });
  test("reads dashed keys and empty values", () => {
    expect(parseTagAttrs('<table header-row="true" fit-page-width="">')).toEqual({
      "header-row": "true",
      "fit-page-width": "",
    });
  });
  test("a bare tag has no attrs", () => {
    expect(parseTagAttrs("<columns>")).toEqual({});
  });
});
