import MarkdownIt from "markdown-it";
import { describe, expect, it } from "vitest";

import { findingSlots } from "./agent-finding-slots";

const markdown = new MarkdownIt({ linkify: true }).use(findingSlots);
const slot = (id: string): string =>
  `<div class="agent-finding-slot" data-finding="${id}">[[${id}]]</div>\n`;

describe("finding slots", () => {
  it("turns a line that is only [[id]] into a slot, splitting the paragraph around it", () => {
    expect(markdown.render("The cause:\n[[store.rs]]\nand then\n\n[[b]]\n[[c]]")).toBe(
      `<p>The cause:</p>\n${slot("store.rs")}<p>and then</p>\n${slot("b")}${slot("c")}`,
    );
  });

  it("places a slot inside a list item", () => {
    expect(markdown.render("- first\n\n  [[a]]")).toContain(`<p>first</p>\n${slot("a")}`);
  });

  it("leaves everything else as the agent wrote it", () => {
    const literal = [
      "see [[a]] inline",
      "[[a]] trailing words",
      "[[weavie:no-reply]]",
      `[[${"x".repeat(65)}]]`,
      "`[[a]]`",
    ];
    for (const text of literal) expect(markdown.render(text)).not.toContain("agent-finding-slot");
    expect(markdown.render("```\n[[a]]\n```")).toBe("<pre><code>[[a]]\n</code></pre>\n");
  });
});
