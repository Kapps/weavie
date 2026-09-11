import type { Locator, Page } from "@playwright/test";
import { expectRevealed } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { awaitReviewSet } from "../harness/navigator";
import { appliedEdit } from "../harness/review";

const sourceName = "definition-review.ts";
const baseline = [
  `export const hiddenDefinition = (value: string) => value; // ${"wide definition ".repeat(100)}`,
  ...Array.from({ length: 178 }, (_, index) => `export const untouched${index} = ${index};`),
  'hiddenDefinition("original call");',
];

test.use({
  fakeScript: {
    steps: [
      { op: "edit", path: `{{WORKSPACE}}/${sourceName}`, content: baseline.join("\n") },
      ...appliedEdit(
        sourceName,
        baseline
          .map((line, index) => (index === 179 ? 'hiddenDefinition("review call");' : line))
          .join("\n"),
      ),
      ...appliedEdit(
        "z-more-review.ts",
        Array.from({ length: 100 }, (_, index) => `export const pending${index} = ${index};`).join(
          "\n",
        ),
      ),
    ],
  },
});

async function prepareDefinition(page: Page): Promise<Locator> {
  await awaitReviewSet(page, [sourceName, "z-more-review.ts"]);
  await page.locator(".editor-empty-review").click();
  const section = page.locator(".unified-review-file", {
    has: page.locator(".unified-review-file-name", { hasText: sourceName }),
  });
  await expect(
    section.locator(".view-line", { hasText: "export const hiddenDefinition" }),
  ).toHaveCount(0);
  const word = section
    .locator(".view-line", { hasText: "review call" })
    .locator("span", { hasText: "hiddenDefinition" })
    .last();
  await expect(word).toBeInViewport();
  await page.evaluate((name) => {
    const monaco = (window as unknown as { __WEAVIE_MONACO__: typeof import("monaco-editor") })
      .__WEAVIE_MONACO__;
    monaco.languages.registerDefinitionProvider("*", {
      provideDefinition: (model) =>
        model.uri.path.endsWith(`/${name}`)
          ? [
              {
                uri: model.uri,
                range: { startLineNumber: 1, startColumn: 14, endLineNumber: 1, endColumn: 30 },
              },
            ]
          : [],
    });
  }, sourceName);
  return word;
}

async function peekScroll(page: Page): Promise<{ top: number; left: number }> {
  return page.evaluate(() => {
    const monaco = (window as unknown as { __WEAVIE_MONACO__: typeof import("monaco-editor") })
      .__WEAVIE_MONACO__;
    const editor = monaco.editor
      .getEditors()
      .find((candidate) => candidate.getDomNode()?.closest(".peekview-widget"));
    if (editor === undefined) throw new Error("Definition peek editor is missing");
    return { top: editor.getScrollTop(), left: editor.getScrollLeft() };
  });
}

test("Go to Definition opens an unchanged same-file definition hidden outside the review hunk", async ({
  page,
}) => {
  const word = await prepareDefinition(page);
  await word.click();
  await page.keyboard.press("F12");
  await expect(page.locator(".unified-review")).toHaveCount(0);
  await expectRevealed(page, sourceName, 1);
  await expect(
    page.locator(".editor .view-line", { hasText: "export const hiddenDefinition" }),
  ).toBeInViewport();
});

test("Alt+click definition peek owns vertical and horizontal scrolling inside unified review", async ({
  page,
}) => {
  const word = await prepareDefinition(page);
  await word.click({ modifiers: ["Alt"] });
  const peek = page.locator(".unified-review .peekview-widget");
  const preview = peek.locator(".monaco-editor");
  const review = page.locator(".unified-review-diffs");
  await expect(preview).toBeVisible();
  await expect(
    preview.locator(".view-line", { hasText: "export const hiddenDefinition" }),
  ).toBeVisible();
  // 2026-09-11, https://github.com/Kapps/weavie/actions/runs/34628508765: opening the peek can move the
  // host editor's own scrollTop (revealing its anchor line around the newly inserted peek zone); since
  // review-editor-viewport.ts (PR #839) now defers that correction's Monaco layout() by one rAF instead of
  // applying it inline, a baseline read here can land mid-settle on a loaded runner. Wait one frame so the
  // baseline reflects the settled position, not a transient one the deferred correction still has to catch up to.
  await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(resolve)));
  const reviewTop = await review.evaluate((element) => element.scrollTop);
  const initial = await peekScroll(page);
  await preview.hover();
  // Chromium reports one legacy wheel notch per event, which Monaco scales independently of deltaX/Y.
  for (let notch = 0; notch < 4; notch++) await page.mouse.wheel(500, 0);
  await expect.poll(async () => (await peekScroll(page)).left).toBeGreaterThan(initial.left + 100);
  for (let notch = 0; notch < 4; notch++) await page.mouse.wheel(0, 500);
  await expect.poll(async () => (await peekScroll(page)).top).toBeGreaterThan(initial.top + 100);
  await expect.poll(() => review.evaluate((element) => element.scrollTop)).toBe(reviewTop);

  const vertical = preview.locator(
    ":scope > .overflow-guard > .monaco-scrollable-element > .scrollbar.vertical > .slider",
  );
  await expect(vertical).toBeVisible();
  const rootScrollbar = page.locator(
    ".unified-review-file:has(.peekview-widget) .unified-review-editor-viewport > .monaco-editor > .overflow-guard > .monaco-scrollable-element > .scrollbar.vertical",
  );
  await expect(rootScrollbar).toHaveCount(1);
  await expect(rootScrollbar).toBeHidden();
  const thumb = await vertical.boundingBox();
  if (thumb === null) throw new Error("Definition peek scrollbar is missing");
  const beforeDrag = await peekScroll(page);
  await page.mouse.move(thumb.x + thumb.width / 2, thumb.y + thumb.height / 2);
  await page.mouse.down();
  await page.mouse.move(thumb.x + thumb.width / 2, thumb.y + thumb.height / 2 + 35, { steps: 5 });
  await page.mouse.up();
  await expect.poll(async () => (await peekScroll(page)).top).toBeGreaterThan(beforeDrag.top);
  await expect.poll(() => review.evaluate((element) => element.scrollTop)).toBe(reviewTop);

  await page.keyboard.press("Escape");
  await expect(peek).toHaveCount(0);
  await word.hover();
  await page.mouse.wheel(0, 500);
  await expect
    .poll(() => review.evaluate((element) => element.scrollTop))
    .toBeGreaterThan(reviewTop);
});
