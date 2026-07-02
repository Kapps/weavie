import { describe, expect, test } from "vitest";
import COMPLETE_EXAMPLE from "./corpus/complete-example.md?raw";
import KITCHEN_SINK from "./corpus/kitchen-sink.md?raw";
import REAL_PAGE from "./corpus/real-page.md?raw";
import { blockSource } from "./notion-edit";
import { renderNotionMarkdown } from "./notion-markdown";
import { SOURCE_ALLOWED_TAGS } from "./source-html";

// The golden corpus: full fixture pages (see corpus/README.md) rendered and snapshotted. A snapshot diff is the
// review surface for any renderer change; grow the corpus from real GET /pages/{id}/markdown responses.
const CORPUS = {
  "complete-example": COMPLETE_EXAMPLE,
  "kitchen-sink": KITCHEN_SINK,
  "real-page": REAL_PAGE,
};

describe("golden corpus", () => {
  for (const [name, markdown] of Object.entries(CORPUS)) {
    test(`renders ${name}`, () => {
      expect(renderNotionMarkdown(markdown)).toMatchSnapshot();
    });
  }
});

// The sanitizer's real allowlist — the renderer must never emit outside it, or DOMPurify silently drops content.
const ALLOWED_TAGS = new Set(SOURCE_ALLOWED_TAGS);

describe("seam invariants", () => {
  test("every emitted tag is on the sanitizer allowlist", () => {
    for (const markdown of Object.values(CORPUS)) {
      const html = renderNotionMarkdown(markdown);
      for (const [, tag] of html.matchAll(/<\/?([a-z0-9]+)/g)) {
        expect(ALLOWED_TAGS.has(tag ?? ""), `tag <${tag}> is not sanitizer-allowed`).toBe(true);
      }
    }
  });

  test("every data-wv-line stamp resolves through the edit path to its verbatim source line", () => {
    for (const markdown of Object.values(CORPUS)) {
      const html = renderNotionMarkdown(markdown);
      const lines = markdown.split("\n");
      const stamps = [...html.matchAll(/data-wv-line="(\d+)"/g)].map((m) => Number(m[1]));
      expect(stamps.length).toBeGreaterThan(0);
      for (const line of stamps) {
        const source = blockSource(markdown, line);
        // The edit path's core invariant: tabs + display + attrs reassemble the fetched line byte-exactly.
        expect(source.tabs + source.display + source.attrsRaw).toBe(lines[line]);
        expect(source.display.trim().length).toBeGreaterThan(0);
      }
    }
  });

  test("only editable element kinds carry stamps; fences and tables never do", () => {
    const html = renderNotionMarkdown(KITCHEN_SINK);
    for (const [, tag] of html.matchAll(/<([a-z0-9]+)[^>]*data-wv-line/g)) {
      expect(["p", "h1", "h2", "h3", "h4", "h5", "h6", "li", "blockquote"]).toContain(tag);
    }
    expect(html).not.toMatch(/<(pre|td|th|table)[^>]*data-wv-line/);
  });

  test("fences keep the shapes highlightCode and mermaid hydration expect", () => {
    const html = renderNotionMarkdown(KITCHEN_SINK);
    expect(html).toContain('<pre class="mermaid-pending">graph TD; A--&gt;B;</pre>');
    expect(renderNotionMarkdown(COMPLETE_EXAMPLE)).toContain('<pre><code class="language-python">');
  });

  test("to-dos render read-only checkboxes with their checked state", () => {
    const html = renderNotionMarkdown(COMPLETE_EXAMPLE);
    expect(html).toContain('<input type="checkbox" checked disabled>');
    expect(html).toContain('<input type="checkbox" disabled>');
  });

  test("the ToC marker renders anchor links to the page's headings", () => {
    const html = renderNotionMarkdown(KITCHEN_SINK);
    expect(html).toContain('<nav class="wv-toc');
    expect(html).toContain('<a href="#wv-h-heading-one">Heading one</a>');
    expect(html).toContain('id="wv-h-heading-one"');
  });

  test("children nest under list items, todos, and quotes instead of flattening", () => {
    const html = renderNotionMarkdown(KITCHEN_SINK);
    expect(html).toMatch(/<li[^>]*>Bullet one.*wv-children.*Paragraph child of bullet one/);
    expect(html).toMatch(/<blockquote[^>]*>Quoted rich text.*wv-children.*Quote child paragraph/);
    expect(html).toMatch(/wv-todo.*Unchecked task.*wv-children.*Task child paragraph/);
  });
});
