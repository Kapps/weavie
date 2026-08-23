import { writeFileSync } from "node:fs";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { focusEditor } from "../harness/navigator";
import type { WeavieWindow } from "../harness/weavie-window";

// comment-prose collapses qualifying comment blocks into prose view-zones, hiding their raw lines. Any
// selection that reaches into a block must therefore leave it raw: otherwise a select-all (or a shift-click
// across a comment) highlights — and copies, or replaces — text the user cannot see. These drive the real
// editor: keyboard/mouse selection against the rendered zones and Monaco's hidden areas.

const DOC_SOURCE = [
  "export const first = 1;",
  "",
  "/**",
  " * Adds two numbers together.",
  " * Handy when you need a sum.",
  " */",
  "export function add(a: number, b: number): number {",
  "  return a + b;",
  "}",
  "",
  "/**",
  " * Formats a name for display.",
  " * Trims the whitespace around it.",
  " */",
  "export function format(name: string): string {",
  "  return name.trim();",
  "}",
  "",
].join("\n");

const prose = (page: Page) => page.locator(".weavie-comment-prose");
const codeLines = (page: Page) => page.locator(".editor-surface .view-lines").first();

const caretLine = (page: Page) =>
  page.evaluate(
    () => (window as WeavieWindow).__WEAVIE_EDITOR__?.getPosition()?.lineNumber ?? null,
  );

// The on-screen vertical extent of the rendered code line holding `text` (Monaco renders spaces as NBSP),
// or null when that line isn't rendered at all — e.g. while it sits hidden under a prose zone.
const lineBand = (page: Page, text: string): Promise<{ top: number; bottom: number } | null> =>
  page.evaluate((needle) => {
    const line = [...document.querySelectorAll(".editor-surface .view-lines .view-line")].find(
      (element) => (element.textContent ?? "").replace(/\u00a0/g, " ").includes(needle),
    );
    if (line === undefined) {
      return null;
    }
    const rect = line.getBoundingClientRect();
    return { top: Math.round(rect.top), bottom: Math.round(rect.bottom) };
  }, text);

// The editor's scroll offset plus the on-screen position of a code line below both comments — the pair that
// moves if a collapse/expand round trip shifts layout.
const anchor = async (page: Page) => ({
  scrollTop: await page.evaluate(
    () => (window as WeavieWindow).__WEAVIE_EDITOR__?.getScrollTop() ?? null,
  ),
  band: await lineBand(page, "export function format"),
});

// Whether a selection highlight actually covers the rendered line holding `text` — i.e. the user can see what
// they selected, instead of it being hidden under a prose zone.
const selectionCovers = async (page: Page, text: string): Promise<boolean> => {
  const band = await lineBand(page, text);
  if (band === null) {
    return false;
  }
  return page.evaluate(
    (line) =>
      [...document.querySelectorAll(".editor-surface .selected-text")].some((highlight) => {
        const rect = highlight.getBoundingClientRect();
        return rect.width > 0 && rect.top < line.bottom && rect.bottom > line.top;
      }),
    band,
  );
};

// Waited on before every keypress: a chord only reaches the editor when it holds keyboard focus. Focus settles
// asynchronously after a click, so this waits for it rather than sampling once — and fails loudly, naming
// focus, if it never lands. The keypress that follows happens once, with its assertions outside any loop.
const awaitEditorFocus = (page: Page) =>
  expect
    .poll(() =>
      page.evaluate(() => (window as WeavieWindow).__WEAVIE_EDITOR__?.hasTextFocus() ?? false),
    )
    .toBe(true);

test("a selection reaching into a comment block leaves it raw", async ({ weavie, page }) => {
  writeFileSync(join(weavie.workspace, "docs.ts"), DOC_SOURCE);
  await openFile(page, "docs.ts");
  await focusEditor(page);

  // Park the caret above the first comment: the harness focus click lands wherever the editor's centre is, so
  // this is what makes the baseline the fully-collapsed state — and it anchors the selection below.
  await page.locator(".view-line", { hasText: "export const first" }).click();

  // Baseline: both doc comments are collapsed to prose, so their raw lines aren't among the code lines.
  await expect(prose(page)).toHaveCount(2);
  await expect(codeLines(page)).not.toContainText("Adds two numbers");
  const before = await anchor(page);

  // Select from the code above the first comment to the code below it (shift-click, the mouse path).
  // Flaked on macOS CI 2026-08-23 04:12 UTC (run 32616592610, job 97139307788): this click's own re-render
  // dropped editor focus and never restored it, timing out the awaitEditorFocus below. Fixed at the source in
  // comment-prose.ts's render(), which now restores focus whenever its own DOM rebuild is what dropped it.
  await page.locator(".view-line", { hasText: "return a + b" }).click({ modifiers: ["Shift"] });

  // The block the selection reaches into goes raw with its text visibly highlighted; the other stays prose.
  await expect(prose(page)).toHaveCount(1);
  await expect(codeLines(page)).toContainText("Adds two numbers");
  expect(await selectionCovers(page, "Adds two numbers")).toBe(true);
  await expect(codeLines(page)).not.toContainText("Formats a name");

  // Select All reaches into every block, so none stays collapsed over selected text.
  await awaitEditorFocus(page);
  await page.keyboard.press("ControlOrMeta+a");
  await expect(prose(page)).toHaveCount(0);
  await expect(codeLines(page)).toContainText("Formats a name");
  expect(await selectionCovers(page, "Formats a name")).toBe(true);

  // Collapsing the selection outside the blocks re-renders both as prose, with layout back where it started.
  await page.locator(".view-line", { hasText: "export const first" }).click();
  await expect(prose(page)).toHaveCount(2);
  await expect(codeLines(page)).not.toContainText("Adds two numbers");
  await expect(codeLines(page)).not.toContainText("Formats a name");
  expect(await anchor(page)).toEqual(before);
});

test("clicking prose and arrowing into a collapsed block still work", async ({ weavie, page }) => {
  writeFileSync(join(weavie.workspace, "docs.ts"), DOC_SOURCE);
  await openFile(page, "docs.ts");
  await focusEditor(page);
  await page.locator(".view-line", { hasText: "export const first" }).click();
  await expect(prose(page)).toHaveCount(2);

  // Clicking a rendered comment opens that block raw for editing, caret inside it.
  await prose(page).first().click();
  await expect(prose(page)).toHaveCount(1);
  await expect(codeLines(page)).toContainText("Adds two numbers");
  expect(await caretLine(page)).toBeGreaterThanOrEqual(3);
  expect(await caretLine(page)).toBeLessThanOrEqual(6);

  // Clicking back out to code re-collapses it.
  await page.locator(".view-line", { hasText: "return a + b" }).click();
  await expect(prose(page)).toHaveCount(2);
  expect(await caretLine(page)).toBe(8);

  // Walk down to the blank line above the second (still collapsed) block, by the keyboard path a user takes.
  await awaitEditorFocus(page);
  await page.keyboard.press("ArrowDown");
  await page.keyboard.press("ArrowDown");
  expect(await caretLine(page)).toBe(10);

  // One more step would clear the whole comment in a single keypress, so it lands the caret INSIDE the block.
  await page.keyboard.press("ArrowDown");
  expect(await caretLine(page)).toBe(11);
  await expect(codeLines(page)).toContainText("Formats a name");
});
