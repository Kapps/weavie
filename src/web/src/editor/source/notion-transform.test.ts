import MarkdownIt from "markdown-it";
import { describe, expect, test } from "vitest";
import {
  installLineStamps,
  normalizeNotionMarkdown,
  normalizeSelfClosing,
  notionColorClass,
  parseTrailingAttrs,
} from "./notion-transform";

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

describe("normalizeSelfClosing", () => {
  test("expands self-closing custom tags to explicit pairs", () => {
    expect(normalizeSelfClosing("<empty-block/>")).toBe("<empty-block></empty-block>");
    expect(normalizeSelfClosing('<mention-date start="2026-01-01"/>')).toBe(
      '<mention-date start="2026-01-01"></mention-date>',
    );
    expect(normalizeSelfClosing("<table_of_contents/>")).toBe(
      "<table_of_contents></table_of_contents>",
    );
    // The markdown API's stand-in for embeds/bookmarks/link previews; left self-closing it would swallow siblings.
    expect(normalizeSelfClosing('<unknown url="https://notion.so/b" alt="embed"/>')).toBe(
      '<unknown url="https://notion.so/b" alt="embed"></unknown>',
    );
  });
  test("leaves paired custom tags alone", () => {
    expect(normalizeSelfClosing('<mention-user url="u">Name</mention-user>')).toBe(
      '<mention-user url="u">Name</mention-user>',
    );
  });
});

describe("normalizeNotionMarkdown", () => {
  test("converts a real Notion page (single-\\n, tab-nested, container tags) to blank-line CommonMark", () => {
    // The exact shape returned by GET /pages/{id}/markdown for the user's test page.
    const input = [
      "<empty-block/>",
      "This is a test page for testing.",
      '<callout icon="💡" color="green_bg">',
      "\tTest Callout",
      "</callout>",
      "### Image",
      "Lets add an image.",
      "![](https://example.com/image.png?a=1&b=2)",
      "<empty-block/>",
      "Coo.",
      "And[ https://google.ca](http://google.ca)",
      '## Testing {toggle="true"}',
      '\t<callout icon="💡" color="green_bg">',
      "\t\tarr",
      "\t</callout>",
    ].join("\n");
    // Each block isolated by a blank line, <empty-block/> dropped, callout children dedented and kept inside.
    const expected = [
      "This is a test page for testing.",
      '<callout icon="💡" color="green_bg">',
      "Test Callout",
      "</callout>",
      "### Image",
      "Lets add an image.",
      "![](https://example.com/image.png?a=1&b=2)",
      "Coo.",
      "And[ https://google.ca](http://google.ca)",
      // The "Testing" toggle heading wraps its indented callout child into a collapsible <details>.
      '<details class="wv-toggle-heading">',
      "<summary>",
      '## Testing {toggle="true"}',
      "</summary>",
      '<callout icon="💡" color="green_bg">',
      "arr",
      "</callout>",
      "</details>",
    ].join("\n\n");
    expect(normalizeNotionMarkdown(input).text).toBe(expected);
  });

  test("keeps a fenced code block intact (never blank-line-splits its lines)", () => {
    const input = ["Intro", "```ts", "const x = 1;", "", "const y = 2;", "```", "Outro"].join("\n");
    expect(normalizeNotionMarkdown(input).text).toBe(
      "Intro\n\n```ts\nconst x = 1;\n\nconst y = 2;\n```\n\nOutro",
    );
  });

  test("keeps a list tight and re-indents nested items as spaces", () => {
    const input = ["- one", "\t- sub", "- two"].join("\n");
    expect(normalizeNotionMarkdown(input)).toEqual({
      text: "- one\n  - sub\n- two",
      lineMap: [0, 1, 2],
    });
  });

  test("keeps a toggle's <summary> attached to <details> so the native toggle works", () => {
    const input = [
      "<details>",
      "\t<summary>Toggle title</summary>",
      "\tHidden body.",
      "</details>",
    ].join("\n");
    // <summary> stays on the open tag (no blank line) — a blank line would let markdown-it wrap it in a <p>.
    expect(normalizeNotionMarkdown(input)).toEqual({
      text: "<details>\n<summary>Toggle title</summary>\n\nHidden body.\n\n</details>",
      lineMap: [0, 1, -1, 2, -1, 3],
    });
  });

  test('a {toggle="true"} heading becomes a <details> wrapping its indented children', () => {
    const input = ['## Section {toggle="true"}', "\tChild line.", "After (dedented)."].join("\n");
    // The heading is the <summary>; its deeper-indented child is the body; the dedented line after is a sibling.
    expect(normalizeNotionMarkdown(input).text).toBe(
      [
        '<details class="wv-toggle-heading">',
        "<summary>",
        '## Section {toggle="true"}',
        "</summary>",
        "Child line.",
        "</details>",
        "After (dedented).",
      ].join("\n\n"),
    );
  });

  test("markdown-it then parses each block — the original bug rendered a whole region as literal text", () => {
    const md = new MarkdownIt({ html: true, linkify: true });
    // A callout immediately followed by content (no blank line) is exactly what used to get swallowed whole.
    const input = [
      "Intro para.",
      '<callout icon="💡" color="green_bg">',
      "\tInside callout.",
      "</callout>",
      "### Heading",
      "Body para.",
    ].join("\n");
    const html = md.render(normalizeNotionMarkdown(input).text);
    expect(html).toContain("<h3>Heading</h3>"); // parsed as a heading, not literal `### Heading`
    expect(html).toContain("<p>Body para.</p>");
    expect(html).toContain('<callout icon="💡" color="green_bg">');
    expect(html).toContain("<p>Inside callout.</p>");
    expect(html).not.toContain("### Heading"); // not swallowed into a raw HTML block
  });

  test("every mapped output line is its original line, dedented — the edit path's core invariant", () => {
    const original = [
      "<empty-block/>",
      "Intro line.",
      '<callout icon="💡" color="green_bg">',
      "\tCallout child",
      "</callout>",
      '## Toggle {toggle="true"}',
      "\tToggle child",
      "- one",
      "\t- sub",
      "```ts",
      "const x = 1;",
      "```",
      "> A quote",
    ];
    const { text, lineMap } = normalizeNotionMarkdown(original.join("\n"));
    const out = text.split("\n");
    expect(lineMap).toHaveLength(out.length);
    for (const [n, orig] of lineMap.entries()) {
      if (orig >= 0) {
        // List items are re-indented (tabs → 2 spaces/level); everything else is the original minus leading tabs.
        expect(out[n]?.trim()).toBe(original[orig]?.trim());
      }
    }
    // The mapped lines land where the edit path needs them: containers' children and toggle-heading children
    // resolve to their own original lines; synthesized wrapper lines stay -1.
    expect(lineMap[out.indexOf("Callout child")]).toBe(3);
    expect(lineMap[out.indexOf('## Toggle {toggle="true"}')]).toBe(5);
    expect(lineMap[out.indexOf("Toggle child")]).toBe(6);
    expect(lineMap[out.indexOf("<summary>")]).toBe(-1);
    expect(lineMap[out.indexOf("> A quote")]).toBe(12);
  });
});

describe("installLineStamps", () => {
  const md = new MarkdownIt({ html: true });
  installLineStamps(md);

  test("stamps blocks with the original line via the lineMap env", () => {
    const { text, lineMap } = normalizeNotionMarkdown(
      ["First para", "## Heading", "- item", "> quote"].join("\n"),
    );
    const html = md.render(text, { lineMap });
    expect(html).toContain('<p data-wv-line="0">First para</p>');
    expect(html).toContain('<h2 data-wv-line="1">Heading</h2>');
    expect(html).toContain('<li data-wv-line="2">item</li>');
    expect(html).toContain('<blockquote data-wv-line="3">');
  });

  test("leaves synthesized lines and fences unstamped", () => {
    const { text, lineMap } = normalizeNotionMarkdown(
      ['## T {toggle="true"}', "\tChild", "```ts", "code", "```"].join("\n"),
    );
    const html = md.render(text, { lineMap });
    expect(html).toContain("<summary>"); // the synthesized wrapper carries no stamp
    expect(html).not.toContain("<summary data-wv-line");
    expect(html).toContain('<p data-wv-line="1">Child</p>');
    expect(html).toContain("<pre>"); // fences are v1 read-only — never stamped
    expect(html).not.toContain("<pre data-wv-line");
  });

  test("renders unstamped without the env — the log viewer's plain render path", () => {
    expect(md.render("Hello")).toBe("<p>Hello</p>\n");
  });
});
