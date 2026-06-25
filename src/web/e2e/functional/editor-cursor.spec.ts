import type { Page } from "@playwright/test";
import { openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// The editor caret is positioned by Monaco from its measured `FontInfo`; the glyphs themselves are flowed by
// the browser using the actually-loaded font. If those diverge (e.g. metrics measured against a fallback before
// the bundled webfont — Go Mono — finished loading, never remeasured), the caret drifts off the characters.
// These tests pin the caret to where the glyphs really are, read from the browser's own layout, and place the
// caret deterministically through the editor handle (window.__WEAVIE_EDITOR__) rather than counting keystrokes.

// The slice of the Monaco editor this spec drives, via the read-only handle the app publishes on window for
// e2e / diagnostics. Declared structurally here so the spec stays self-contained (e2e isn't in the app
// tsconfig, so it doesn't see the app-side global.d.ts declaration).
interface EditorHandle {
  focus(): void;
  setPosition(position: { lineNumber: number; column: number }): void;
  getPosition(): { lineNumber: number; column: number } | null;
  getModel(): { getLineContent(line: number): string } | null;
}
type WeavieWindow = Window & { __WEAVIE_EDITOR__?: EditorHandle };

interface CaretSample {
  /** Viewport x of the painted caret's left edge. */
  caretLeft: number;
  /** Viewport x of the real character boundary the caret should sit on (left edge of the char to its right,
   *  or the right edge of the last char when the caret is at end-of-line). */
  boundary: number;
  /** Advance width of the reference glyph, for diagnostics. */
  charWidth: number;
}

// Focuses the editor and moves the caret to (line, column); returns the actual position Monaco settled on
// (clamped to the line), so callers can pass an over-large column to mean "end of line".
async function placeCaret(
  page: Page,
  line: number,
  column: number,
): Promise<{ line: number; column: number }> {
  const pos = await page.evaluate(
    ({ line, column }) => {
      const editor = (window as WeavieWindow).__WEAVIE_EDITOR__;
      if (editor === undefined) {
        return null;
      }
      editor.focus();
      editor.setPosition({ lineNumber: line, column });
      const p = editor.getPosition();
      return p === null ? null : { line: p.lineNumber, column: p.column };
    },
    { line, column },
  );
  if (pos === null) {
    throw new Error("editor handle not available");
  }
  return pos;
}

// Reads the caret's painted position and the ground-truth glyph boundary for `column` (1-based) on `line`.
// Ground truth comes from a DOM Range over the rendered text, i.e. where the browser actually drew the glyphs.
async function caretVsGlyph(page: Page, line: number, column: number): Promise<CaretSample | null> {
  return page.evaluate(
    ({ line, column }) =>
      new Promise<CaretSample | null>((resolve) => {
        requestAnimationFrame(() =>
          requestAnimationFrame(() => {
            const editorEl = document.querySelector(".monaco-editor");
            const caret = editorEl?.querySelector(".cursors-layer .cursor");
            const lineEls = [
              ...(editorEl?.querySelectorAll(".view-lines .view-line") ?? []),
            ] as HTMLElement[];
            lineEls.sort((a, b) => Number.parseFloat(a.style.top) - Number.parseFloat(b.style.top));
            const lineEl = lineEls[line - 1];
            if (caret === null || caret === undefined || lineEl === undefined) {
              resolve(null);
              return;
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
              resolve(null);
              return;
            }
            resolve({
              caretLeft: caret.getBoundingClientRect().left,
              boundary: atEnd ? ref.right : ref.left,
              charWidth: ref.width,
            });
          }),
        );
      }),
    { line, column },
  );
}

function need<T>(value: T | null, message: string): T {
  if (value === null) {
    throw new Error(message);
  }
  return value;
}

// A settled paint: the editor handle is up (it rides a lazily-loaded chunk, so it can lag the tab appearing),
// the bundled webfont has loaded, and the editor has had a frame to lay out against it.
async function settle(page: Page): Promise<void> {
  await page.waitForFunction(() => (window as WeavieWindow).__WEAVIE_EDITOR__ !== undefined);
  await page.evaluate(() => document.fonts.ready);
  await page.evaluate(
    () => new Promise<void>((r) => requestAnimationFrame(() => requestAnimationFrame(() => r()))),
  );
}

// The length of a model line, via the editor handle.
async function lineLength(page: Page, line: number): Promise<number> {
  const len = await page.evaluate(
    (line) =>
      (window as WeavieWindow).__WEAVIE_EDITOR__?.getModel()?.getLineContent(line).length ?? null,
    line,
  );
  return need(len, "editor handle / model not available");
}

// How far the caret may sit from the true glyph boundary. When aligned the residual is a flat ~1px that
// doesn't grow along the line (a caret-box / sub-pixel constant); the bug drifts ~¼px per column, reaching
// 5–11px by mid/end of line. 2.5px clears the benign floor with margin while still catching that drift.
const TOLERANCE_PX = 2.5;

test("caret stays on the glyph boundary across a line", async ({ page }) => {
  await openFile(page, "hello.ts");
  await settle(page);
  const len = await lineLength(page, 1);
  // Probe the start, several interior columns, and end-of-line (drift grows with column).
  const columns = [...new Set([1, 11, 21, 31, 41, len + 1].filter((c) => c <= len + 1))];

  const deltas: { column: number; delta: number; charWidth: number }[] = [];
  for (const column of columns) {
    const at = await placeCaret(page, 1, column);
    const sample = need(
      await caretVsGlyph(page, at.line, at.column),
      `could not measure the caret at column ${column}`,
    );
    deltas.push({
      column: at.column,
      delta: sample.caretLeft - sample.boundary,
      charWidth: sample.charWidth,
    });
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

  // Insert mid-line (column 20), where any caret drift is large, then type through the real input path.
  await placeCaret(page, 1, 20);
  await page.keyboard.type("X");
  await settle(page);

  // The character landed exactly at the caret column (1-based 20 → 0-based index 19).
  const lineText = await page.evaluate(
    () => (window as WeavieWindow).__WEAVIE_EDITOR__?.getModel()?.getLineContent(1) ?? "",
  );
  expect(lineText[19]).toBe("X");

  // The caret advanced past it (now column 21) and sits on the boundary of the real glyphs.
  const sample = need(await caretVsGlyph(page, 1, 21), "could not measure the caret after typing");
  expect(
    Math.abs(sample.caretLeft - sample.boundary),
    `caret is ${(sample.caretLeft - sample.boundary).toFixed(2)}px off after inserting a character`,
  ).toBeLessThanOrEqual(TOLERANCE_PX);
});

test("autocomplete opens and the caret stays aligned while it is showing", async ({ page }) => {
  await openFile(page, "hello.ts");
  await settle(page);

  // A fresh end-of-file line typed with the prefix of an identifier already in the buffer (`console`), so
  // word-based completion has something deterministic to offer — and the caret is far enough into the line
  // that any drift is well above the measurement floor.
  await placeCaret(page, 9999, 9999);
  await page.keyboard.press("Enter");
  await page.keyboard.type("consol");
  await settle(page);

  await page.keyboard.press("Control+Space");
  const widget = page.locator(".suggest-widget");
  await expect(widget).toBeVisible();
  await expect(widget).toContainText("console");

  // The suggestion anchors to the caret; the caret must be where the typed glyphs actually are.
  const pos = need(
    await page.evaluate(() => {
      const p = (window as WeavieWindow).__WEAVIE_EDITOR__?.getPosition();
      return p === undefined || p === null ? null : { line: p.lineNumber, column: p.column };
    }),
    "could not read caret position",
  );
  const sample = need(
    await caretVsGlyph(page, pos.line, pos.column),
    "could not measure the caret with autocomplete open",
  );
  expect(
    Math.abs(sample.caretLeft - sample.boundary),
    `caret is ${(sample.caretLeft - sample.boundary).toFixed(2)}px off while autocomplete is open`,
  ).toBeLessThanOrEqual(TOLERANCE_PX);
});
