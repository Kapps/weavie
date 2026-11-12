import { describe, expect, it } from "vitest";
import { ITEM_END, ITEM_START, markdownBlocks } from "./markdown-blocks";
import { createMarkdownEngine } from "./markdown-renderer";

const { markdown } = createMarkdownEngine({
  html: "any",
  images: "direct",
  allowMermaid: true,
  safeLinksOnly: false,
});
const blocks = (content: string) => markdownBlocks(markdown, content);

describe("markdownBlocks", () => {
  it("maps each top-level block to its source lines", () => {
    const parsed = blocks("# Title\n\nOne\ntwo\n\n```ts\nx\n```\n");
    expect(parsed.map(({ start, end }) => [start, end])).toEqual([
      [0, 1],
      [2, 4],
      [5, 8],
    ]);
    expect(parsed.map((block) => block.code)).toEqual([false, false, true]);
  });

  it("resolves a reference link defined in another block", () => {
    expect(blocks("[site][a]\n\n[a]: https://example.com\n")[0]?.html).toContain(
      'href="https://example.com"',
    );
  });

  it("stamps list items with their own lines, stopping at a nested list", () => {
    const [list] = blocks("- one\n  - two\n- three\n");
    for (const line of [0, 1, 2])
      expect(list?.html).toContain(`<li ${ITEM_START}="${line}" ${ITEM_END}="${line + 1}">`);
  });

  it("keeps a block's HTML identical when lines above it move", () => {
    expect(blocks("- a\n")[0]?.html).toBe(blocks("x\n\ny\n\n- a\n")[2]?.html);
  });

  it("ignores tags in comments and unpaired tags when joining HTML", () => {
    expect(blocks("<!-- use <b> -->\n\nOne\n\nTwo\n")).toHaveLength(3);
    expect(blocks("<p>open\n\nOne\n\nTwo\n")).toHaveLength(3);
  });

  it("holds an open HTML element and its body together as one block", () => {
    const parsed = blocks("<details>\n<summary>More</summary>\n\nBody\n\n</details>\n\nAfter\n");
    expect(parsed).toHaveLength(2);
    expect(parsed[0]?.html).toContain("<p>Body</p>");
    expect(parsed[0]?.html).toContain("</details>");
  });
});
