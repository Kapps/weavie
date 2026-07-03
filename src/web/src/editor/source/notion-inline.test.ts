import { describe, expect, test } from "vitest";
import { renderInline } from "./notion-inline";

describe("renderInline", () => {
  test("renders the standard marks", () => {
    expect(renderInline("**b** *i* ~~s~~ `c`")).toBe(
      "<strong>b</strong> <em>i</em> <s>s</s> <code>c</code>",
    );
  });

  test("nests marks and renders links with recursed labels", () => {
    expect(renderInline("**bold *inner***")).toBe("<strong>bold <em>inner</em></strong>");
    expect(renderInline("***both***")).toBe("<strong><em>both</em></strong>");
    expect(renderInline("[**go**](https://e.com)")).toBe(
      '<a href="https://e.com"><strong>go</strong></a>',
    );
  });

  test("resolves Notion's escapes to literal characters", () => {
    expect(renderInline("\\*not bold\\*")).toBe("*not bold*");
    expect(renderInline("\\<not a tag\\>")).toBe("&lt;not a tag&gt;");
    expect(renderInline("\\$5 and \\$6")).toBe("$5 and $6");
    expect(renderInline("a\\|pipe \\{brace\\} \\^caret \\\\slash")).toBe(
      "a|pipe {brace} ^caret \\slash",
    );
  });

  test("HTML-escapes everything unrecognized — unknown tags never pass through", () => {
    expect(renderInline('<script>alert("x")</script>')).toBe(
      "&lt;script&gt;alert(&quot;x&quot;)&lt;/script&gt;",
    );
    expect(renderInline("a & b < c")).toBe("a &amp; b &lt; c");
  });

  test("inline math renders styled, code spans keep $ literal", () => {
    expect(renderInline("$e=mc^2$")).toBe('<code class="wv-math">e=mc^2</code>');
    expect(renderInline("`cost: $5`")).toBe("<code>cost: $5</code>");
  });

  test("unmatched delimiters stay literal text", () => {
    expect(renderInline("2 * 3 and 5 $ left `open")).toBe("2 * 3 and 5 $ left `open");
  });

  test("span colors, backgrounds, and underline map to wv-* classes", () => {
    expect(renderInline('<span color="green">g</span>')).toBe(
      '<span class="wv-color-green">g</span>',
    );
    expect(renderInline('<span color="yellow_bg">y</span>')).toBe(
      '<span class="wv-bg-yellow">y</span>',
    );
    expect(renderInline('<span underline="true">u</span>')).toBe(
      '<span class="wv-underline">u</span>',
    );
  });

  test("a span with no recognized attrs unwraps to its content", () => {
    expect(renderInline('<span color="chartreuse">x</span>')).toBe("x");
  });

  test("inline images render; a bare ! stays literal", () => {
    expect(renderInline("A picture: ![pic](https://e.com/i.png)")).toBe(
      'A picture: <img src="https://e.com/i.png" alt="pic">',
    );
    expect(renderInline("Hello! [link](https://e.com)")).toBe(
      'Hello! <a href="https://e.com">link</a>',
    );
  });

  test("<br> becomes a line break", () => {
    expect(renderInline("Line 1<br>Line 2<br/>Line 3")).toBe("Line 1<br>Line 2<br>Line 3");
  });

  test("mentions link when they carry a url, else render their text", () => {
    expect(renderInline('<mention-user url="https://e.com/u">Ada</mention-user>')).toBe(
      '<a href="https://e.com/u">Ada</a>',
    );
    expect(renderInline("<mention-user>Ada</mention-user>")).toBe("Ada");
  });

  test("date mentions render their range", () => {
    expect(renderInline('<mention-date start="2026-07-01" end="2026-07-04"/>')).toBe(
      "2026-07-01 → 2026-07-04",
    );
    expect(
      renderInline('<mention-date start="2026-07-02" startTime="09:00" timeZone="UTC"/>'),
    ).toBe("2026-07-02 09:00");
  });

  test("citations stay literal text (v1)", () => {
    expect(renderInline("claim.[^https://e.com/src]")).toBe("claim.[^https://e.com/src]");
  });

  test("attribute injection can't escape a href", () => {
    // The url stops at the first `)`; whatever lands in the attribute is escaped, the rest is literal text.
    expect(renderInline('[x](https://e.com/"><img src=x onerror=alert(1)>)')).toBe(
      '<a href="https://e.com/&quot;&gt;&lt;img src=x onerror=alert(1">x</a>&gt;)',
    );
  });
});
