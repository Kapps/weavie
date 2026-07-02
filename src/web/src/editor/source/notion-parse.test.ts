import { describe, expect, test } from "vitest";
import { parseNotion } from "./notion-parse";

describe("parseNotion", () => {
  test("parses the one-block-per-line dialect with original line indices", () => {
    const blocks = parseNotion(["First para", "## Heading", "- item", "> quote"].join("\n"));
    expect(blocks.map((b) => [b.kind, b.line])).toEqual([
      ["paragraph", 0],
      ["heading", 1],
      ["bulleted", 2],
      ["quote", 3],
    ]);
  });

  test("skips blank lines and <empty-block/> spacers", () => {
    const blocks = parseNotion(["A", "", "<empty-block/>", "B"].join("\n"));
    expect(blocks.map((b) => [b.kind, b.line])).toEqual([
      ["paragraph", 0],
      ["paragraph", 3],
    ]);
  });

  test("tab-indented lines nest as children under ANY block kind", () => {
    const blocks = parseNotion(
      [
        "Parent para",
        "\tChild para",
        "- [ ] task",
        "\tTask child",
        "> quote",
        "\tQuote child",
      ].join("\n"),
    );
    expect(blocks.map((b) => b.kind)).toEqual(["paragraph", "todo", "quote"]);
    for (const [i, childLine] of [1, 3, 5].entries()) {
      const block = blocks[i];
      expect("children" in block! && block.children.map((c) => [c.kind, c.line])).toEqual([
        ["paragraph", childLine],
      ]);
    }
  });

  test("to-dos carry their checked state", () => {
    const blocks = parseNotion('- [ ] open {color="gray"}\n- [x] done');
    expect(blocks).toMatchObject([
      { kind: "todo", checked: false, color: "gray", text: "open" },
      { kind: "todo", checked: true, color: null, text: "done" },
    ]);
  });

  test("containers recurse (dedenting) and keep their own line for the open tag", () => {
    const blocks = parseNotion(
      ['<callout icon="💡" color="green_bg">', "\tInside", "</callout>", "After"].join("\n"),
    );
    expect(blocks).toMatchObject([
      {
        kind: "callout",
        line: 0,
        icon: "💡",
        color: "green_bg",
        children: [{ kind: "paragraph", line: 1, text: "Inside" }],
      },
      { kind: "paragraph", line: 3 },
    ]);
  });

  test("a toggle's <summary> becomes the summary, not a child", () => {
    const blocks = parseNotion(
      ['<details color="pink">', "<summary>Title</summary>", "\tBody.", "</details>"].join("\n"),
    );
    expect(blocks).toMatchObject([
      { kind: "toggle", color: "pink", summary: "Title", children: [{ kind: "paragraph" }] },
    ]);
  });

  test("a toggle heading keeps its deeper-indented children", () => {
    const blocks = parseNotion(
      ['## Section {toggle="true"}', "\tChild line.", "After (dedented)."].join("\n"),
    );
    expect(blocks).toMatchObject([
      { kind: "heading", level: 2, toggle: true, children: [{ kind: "paragraph", line: 1 }] },
      { kind: "paragraph", line: 2 },
    ]);
  });

  test("fences keep their lines verbatim (blank lines included) and never nest", () => {
    const blocks = parseNotion(["```ts", "const x = 1;", "", "const y = 2;", "```"].join("\n"));
    expect(blocks).toEqual([
      { kind: "fence", line: 0, lang: "ts", code: "const x = 1;\n\nconst y = 2;" },
    ]);
  });

  test("an indented fence under a list item strips only the nesting tabs", () => {
    const blocks = parseNotion(["- item", "\t```py", "\t\tindented code", "\t```"].join("\n"));
    expect(blocks).toMatchObject([
      { kind: "bulleted", children: [{ kind: "fence", lang: "py", code: "\tindented code" }] },
    ]);
  });

  test("block equations parse multi-line and single-line", () => {
    expect(parseNotion("$$\nx^2\n$$")).toEqual([{ kind: "equation", line: 0, tex: "x^2" }]);
    expect(parseNotion("$$e=mc^2$$")).toEqual([{ kind: "equation", line: 0, tex: "e=mc^2" }]);
  });

  test("leaf tags become cards; <unknown/> names its block type", () => {
    const blocks = parseNotion(
      [
        '<page url="https://notion.so/x">Sub page</page>',
        '<unknown url="https://notion.so/y" alt="link_preview"/>',
      ].join("\n"),
    );
    expect(blocks).toMatchObject([
      { kind: "card", tag: "page", url: "https://notion.so/x", text: "Sub page" },
      { kind: "card", tag: "unknown", url: "https://notion.so/y", text: "link preview" },
    ]);
  });

  test("numbered items keep their number; images split url and caption", () => {
    expect(parseNotion("3. third")).toMatchObject([{ kind: "numbered", number: 3 }]);
    expect(parseNotion("![Cap](https://e.com/i.png)")).toMatchObject([
      { kind: "image", url: "https://e.com/i.png", caption: "Cap" },
    ]);
  });

  test("an unclosed container consumes the rest instead of vanishing", () => {
    const blocks = parseNotion(["<callout>", "\tOrphan"].join("\n"));
    expect(blocks).toMatchObject([{ kind: "callout", children: [{ kind: "paragraph" }] }]);
  });
});
