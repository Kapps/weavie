import type { Page } from "@playwright/test";
import { openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// The editor caret is positioned by Monaco from its measured `FontInfo`; the glyphs themselves are flowed by
// the browser using the actually-loaded font. If those diverge (e.g. metrics measured against a fallback before
// the bundled webfont — Go Mono — finished loading, never remeasured), the caret drifts off the characters.
// These tests pin the caret to where the glyphs really are, read from the browser's own layout.

interface CaretSample {
  /** Viewport x of the painted caret's left edge. */
  caretLeft: number;
  /** Viewport x of the real character boundary the caret should sit on (left edge of the char to its right,
   *  or the right edge of the last char when the caret is at end-of-line). */
  boundary: number;
  /** Advance width of the reference glyph, for tolerance scaling / diagnostics. */
  charWidth: number;
  /** Character count of the measured line. */
  lineLen: number;
}

// Reads the caret's painted position and the ground-truth glyph boundary for `column` (1-based) on `line`.
// Ground truth comes from a DOM Range over the rendered text, i.e. where the browser actually drew the glyphs.
async function caretVsGlyph(page: Page, line: number, column: number): Promise<CaretSample | null> {
  return page.evaluate(
    ({ line, column }) => {
      const editor = document.querySelector(".monaco-editor");
      const caret = editor?.querySelector(".cursors-layer .cursor");
      const lineEls = [
        ...(editor?.querySelectorAll(".view-lines .view-line") ?? []),
      ] as HTMLElement[];
      lineEls.sort((a, b) => Number.parseFloat(a.style.top) - Number.parseFloat(b.style.top));
      const lineEl = lineEls[line - 1];
      if (caret === null || caret === undefined || lineEl === undefined) {
        return null;
      }
      // Pixel box of the `index`-th character (0-based) as the browser laid it out.
      const charRect = (index: number): DOMRect | null => {
        const walker = document.createTreeWalker(lineEl, NodeFilter.SHOW_TEXT);
        let remaining = index;
        let node = walker.nextNode();
        while (node !== null) {
          const len = node.nodeValue?.length ?? 0;
          if (remaining < len) {
            const range = document.createRange();
            range.setStart(node, remaining);
            range.setEnd(node, remaining + 1);
            return range.getBoundingClientRect();
          }
          remaining -= len;
          node = walker.nextNode();
        }
        return null;
      };
      const lineLen = (lineEl.textContent ?? "").length;
      const atEnd = column - 1 >= lineLen;
      const ref = charRect(atEnd ? lineLen - 1 : column - 1);
      if (ref === null) {
        return null;
      }
      return {
        caretLeft: caret.getBoundingClientRect().left,
        boundary: atEnd ? ref.right : ref.left,
        charWidth: ref.width,
        lineLen,
      };
    },
    { line, column },
  );
}

function need<T>(value: T | null, message: string): T {
  if (value === null) {
    throw new Error(message);
  }
  return value;
}

// A settled paint: the bundled webfont has loaded and the editor has had a frame to lay out against it.
async function settle(page: Page): Promise<void> {
  await page.evaluate(() => document.fonts.ready);
  await page.evaluate(
    () => new Promise<void>((r) => requestAnimationFrame(() => requestAnimationFrame(() => r()))),
  );
}

// How far the caret may sit from the true glyph boundary. Correct alignment is sub-pixel; the bug under test
// drifts by a meaningful fraction of a character, so this cleanly separates the two.
const TOLERANCE_PX = 1.5;

test("caret stays on the glyph boundary across a line", async ({ page }) => {
  await openFile(page, "hello.ts");
  await settle(page);
  await page.locator(".monaco-editor .view-lines").click();
  await page.keyboard.press("ControlOrMeta+Home");
  await settle(page);

  const start = need(await caretVsGlyph(page, 1, 1), "could not measure the caret at line start");
  const len = start.lineLen;
  // Probe the start, two interior columns, and end-of-line (where accumulated metric drift is largest).
  const columns = [...new Set([1, Math.floor(len / 3), Math.floor((2 * len) / 3), len + 1])].sort(
    (a, b) => a - b,
  );

  const deltas: { column: number; delta: number; charWidth: number }[] = [];
  let at = 1;
  for (const column of columns) {
    while (at < column) {
      await page.keyboard.press("ArrowRight");
      at += 1;
    }
    const sample = need(
      await caretVsGlyph(page, 1, column),
      `could not measure the caret at column ${column}`,
    );
    deltas.push({ column, delta: sample.caretLeft - sample.boundary, charWidth: sample.charWidth });
  }

  console.log("caret-vs-glyph deltas (px):", JSON.stringify(deltas));
  for (const { column, delta, charWidth } of deltas) {
    expect(
      Math.abs(delta),
      `column ${column}: caret is ${delta.toFixed(2)}px off the glyph boundary (charWidth ${charWidth.toFixed(2)}px)`,
    ).toBeLessThanOrEqual(TOLERANCE_PX);
  }
});

test("a typed character is inserted at the caret and the caret stays aligned", async ({ page }) => {
  await openFile(page, "hello.ts");
  await settle(page);
  await page.locator(".monaco-editor .view-lines").click();
  await page.keyboard.press("ControlOrMeta+Home");
  await settle(page);

  await page.keyboard.type("Z");
  await page.evaluate(() => new Promise<void>((r) => requestAnimationFrame(() => r())));

  // The character lands at the caret: line 1 now begins with it.
  const lineText = await page.evaluate(() => {
    const lines = [
      ...document.querySelectorAll(".monaco-editor .view-lines .view-line"),
    ] as HTMLElement[];
    lines.sort((a, b) => Number.parseFloat(a.style.top) - Number.parseFloat(b.style.top));
    return lines[0]?.textContent ?? "";
  });
  expect(lineText.startsWith("Z")).toBe(true);

  // The caret advanced past it and sits on the boundary of the real glyphs (column 2 = after "Z").
  const sample = need(await caretVsGlyph(page, 1, 2), "could not measure the caret after typing");
  expect(
    Math.abs(sample.caretLeft - sample.boundary),
    `caret is ${(sample.caretLeft - sample.boundary).toFixed(2)}px off after inserting a character`,
  ).toBeLessThanOrEqual(TOLERANCE_PX);
});

test("autocomplete opens and the caret stays aligned while it is showing", async ({ page }) => {
  await openFile(page, "hello.ts");
  await settle(page);
  await page.locator(".monaco-editor .view-lines").click();
  // A fresh line whose prefix matches an identifier already in the buffer (`greet`), so word-based
  // completion has something deterministic to offer.
  await page.keyboard.press("ControlOrMeta+End");
  await page.keyboard.press("Enter");
  await page.keyboard.type("gre");
  await page.evaluate(() => new Promise<void>((r) => requestAnimationFrame(() => r())));

  await page.keyboard.press("Control+Space");
  const widget = page.locator(".suggest-widget");
  await expect(widget).toBeVisible();
  await expect(widget).toContainText("greet");

  // The suggestion anchors to the caret; the caret must be where the typed glyphs actually are. Find the
  // line that now holds "gre" and check column 4 (just after it).
  const line = await page.evaluate(() => {
    const lines = [
      ...document.querySelectorAll(".monaco-editor .view-lines .view-line"),
    ] as HTMLElement[];
    lines.sort((a, b) => Number.parseFloat(a.style.top) - Number.parseFloat(b.style.top));
    return lines.findIndex((el) => (el.textContent ?? "").startsWith("gre")) + 1;
  });
  expect(line).toBeGreaterThan(0);
  const sample = need(
    await caretVsGlyph(page, line, 4),
    "could not measure the caret with autocomplete open",
  );
  expect(
    Math.abs(sample.caretLeft - sample.boundary),
    `caret is ${(sample.caretLeft - sample.boundary).toFixed(2)}px off while autocomplete is open`,
  ).toBeLessThanOrEqual(TOLERANCE_PX);
});
