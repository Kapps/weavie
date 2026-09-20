import type { Locator } from "@playwright/test";
import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";
import { reviewScroll, scrollReview } from "../harness/review-scroll";

const paths = ["a-first.txt", "b-second.txt", "c-third.txt"];
test.use({
  fakeScript: {
    steps: paths.flatMap((path) =>
      appliedEdit(
        path,
        Array.from({ length: 35 }, (_, index) => `${path} line ${index}`).join("\n"),
      ),
    ),
  },
});

test("review viewport follows resizing and a preceding file's collapse", async ({ page }) => {
  await expect(page.locator(".editor-empty-review")).toContainText("3");
  await page.locator(".editor-empty-review").click();
  await page.locator(".unified-review-tree-row.file").first().click();
  const scroller = page.locator(".unified-review-diffs");
  const viewport = scroller.locator(":scope > .monaco-scrollable-element");
  const first = page.locator(".unified-review-file", {
    has: page.locator(".unified-review-file-name", { hasText: paths[0] }),
  });
  const second = page.locator(".unified-review-file", {
    has: page.locator(".unified-review-file-name", { hasText: paths[1] }),
  });
  await expect(first.locator(".view-line").first()).toBeVisible();
  const assertAlignment = async (section: Locator): Promise<void> => {
    const header = section.locator(".unified-review-file-header");
    const start = await header.boundingBox();
    const bounds = await viewport.boundingBox();
    if (start === null || bounds === null) throw new Error("Review viewport is missing");
    const target = (await reviewScroll(page)).top + start.y - bounds.y + 100;
    while ((await reviewScroll(page)).top < target) {
      await page.getByRole("scrollbar", { name: "Review scroll position" }).press("ArrowDown");
    }
    await expect.poll(() => reviewScroll(page).then(({ top }) => top)).toBeGreaterThan(0);
    await expect
      .poll(async () => {
        const heading = await header.boundingBox();
        const view = await viewport.boundingBox();
        if (heading === null || view === null) return Number.POSITIVE_INFINITY;
        return Math.abs(heading.y - view.y);
      })
      .toBeLessThan(2);
    await expect
      .poll(async () => {
        const editor = await section.locator(".monaco-editor").boundingBox();
        const heading = await header.boundingBox();
        if (editor === null || heading === null) return Number.POSITIVE_INFINITY;
        return Math.abs(editor.y - heading.y - heading.height);
      })
      .toBeLessThan(2);
  };
  for (const width of [700, 1600]) {
    await page.setViewportSize({ width, height: 800 });
    if (width === 700) await page.getByRole("button", { name: "Code", exact: true }).click();
    await assertAlignment(first);
    await scrollReview(page, "start");
    await expect.poll(() => reviewScroll(page).then(({ top }) => top)).toBe(0);
  }
  await expect(second).toBeAttached();
  const secondTop = () => second.evaluate((element) => Number.parseFloat(element.style.top));
  const expandedTop = await secondTop();
  await first.locator(".unified-review-file-toggle").click();
  await expect(first).toHaveClass(/collapsed/);
  await expect.poll(secondTop).toBeLessThan(expandedTop);
  await expect(second.locator(".view-line").first()).toBeVisible();
  await assertAlignment(second);
  await scrollReview(page, "start");
  await first.locator(".unified-review-file-toggle").click();
  await expect(first).not.toHaveClass(/collapsed/);
  await expect.poll(secondTop).toBe(expandedTop);
  await expect(first.locator(".view-line").first()).toBeVisible();
});
