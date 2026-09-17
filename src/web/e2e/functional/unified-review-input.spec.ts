import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";
import { reviewScroll } from "../harness/review-scroll";
import type { EditorHandle, WeavieWindow } from "../harness/weavie-window";

test.use({
  fakeScript: {
    steps: appliedEdit(
      "controlled-input.txt",
      Array.from({ length: 5_000 }, (_, index) => `Review line ${index + 1}`).join("\n"),
    ),
  },
});

test("controlled review preserves scroll-only commands and reveals distant find results", async ({
  page,
}) => {
  await page.locator(".editor-empty-review").click();
  const section = page.locator(".unified-review-file");
  await expect(section.locator(".weavie-inline-newfile-tag")).toHaveText("New file");
  await section.locator(".view-line", { hasText: /^Review\sline\s1$/ }).click();
  const editor = await page.evaluateHandle(() => {
    const editors = (
      window as WeavieWindow
    ).__WEAVIE_MONACO__?.editor.getEditors() as EditorHandle[];
    const review = editors.find((candidate) =>
      candidate.getModel()?.uri.path.endsWith("/controlled-input.txt"),
    );
    if (review === undefined) throw new Error("Review editor is missing");
    return review as EditorHandle & {
      _commandService: { executeCommand(command: string): Promise<void> };
    };
  });
  const position = () => editor.evaluate((instance) => instance.getPosition());
  // Weavie's default Ctrl+arrows navigate hunks; dispatch native scroll commands as a rebound key does.
  const scroll = (command: string) =>
    editor.evaluate((instance, id) => instance._commandService.executeCommand(id), command);
  const checkScrollOnly = async () => {
    const before = await position();
    const initialScroll = await reviewScroll(page).then(({ top }) => top);
    await scroll("scrollLineDown");
    await expect
      .poll(() => reviewScroll(page).then(({ top }) => top))
      .toBeGreaterThan(initialScroll);
    expect(await position()).toEqual(before);
    await scroll("scrollLineUp");
    await expect.poll(() => reviewScroll(page).then(({ top }) => top)).toBe(initialScroll);
    expect(await position()).toEqual(before);
  };
  // Native editor scrolling first aligns the file below its pinned header.
  await scroll("scrollLineDown");
  await checkScrollOnly();

  for (const line of [4500, 4980]) {
    await page.keyboard.press("ControlOrMeta+f");
    const find = section.locator(".find-widget textarea").first();
    await find.fill(`Review line ${line}`);
    const match = section.locator(".view-line", {
      hasText: new RegExp(`^Review\\sline\\s${line}$`),
    });
    await expect(match).toBeInViewport();
    await page.keyboard.press("Escape");
    await expect.poll(position).toMatchObject({ lineNumber: line });
    const headerBottom = await section
      .locator(".unified-review-file-header")
      .evaluate((element) => element.getBoundingClientRect().bottom);
    const toolbarTop = await page
      .locator(".weavie-inline-toolbar")
      .evaluate((element) => element.getBoundingClientRect().top);
    const matchBounds = await match.boundingBox();
    if (matchBounds === null) throw new Error("Find result was not rendered");
    expect(matchBounds.y).toBeGreaterThanOrEqual(headerBottom);
    expect(matchBounds.y + matchBounds.height).toBeLessThanOrEqual(toolbarTop);
    expect(await section.locator(".view-line").count()).toBeLessThan(250);
    await checkScrollOnly();
  }
  const scrollbar = page.getByRole("scrollbar", { name: "Review scroll position" });
  await scrollbar.focus();
  await page.keyboard.press("End");
  await expect
    .poll(async () => {
      const { top, maximum } = await reviewScroll(page);
      return maximum - top;
    })
    .toBeLessThanOrEqual(1);
  await expect(section.locator(".view-line", { hasText: /^Review\sline\s5000$/ })).toBeInViewport();
  await expect(section.locator(".unified-review-file-header")).toBeInViewport();
  await page.keyboard.press("Home");
  await expect.poll(async () => (await reviewScroll(page)).top).toBe(0);
  await expect(section.locator(".view-line", { hasText: /^Review\sline\s1$/ })).toBeInViewport();
  const thumb = scrollbar.locator(".slider");
  const trackBounds = await scrollbar.boundingBox();
  const thumbBounds = await thumb.boundingBox();
  if (trackBounds === null || thumbBounds === null) throw new Error("Review scrollbar is missing");
  await page.mouse.move(
    thumbBounds.x + thumbBounds.width / 2,
    thumbBounds.y + thumbBounds.height / 2,
  );
  await page.mouse.down();
  await page.mouse.move(
    thumbBounds.x + thumbBounds.width / 2,
    trackBounds.y + trackBounds.height / 2,
    { steps: 12 },
  );
  await page.mouse.up();
  await expect.poll(async () => (await reviewScroll(page)).top).toBeGreaterThan(0);
  await expect(section.locator(".view-line").first()).toBeInViewport();
  await expect(section.locator(".unified-review-file-header")).toBeInViewport();
});

test.describe("partially visible small file", () => {
  test.use({
    fakeScript: {
      steps: ["a-small.txt", "b-small.txt", "c-small.txt"].flatMap((path) =>
        appliedEdit(
          path,
          Array.from(
            { length: 20 },
            (_, index) => `Small line ${String(index + 1).padStart(2, "0")}`,
          ).join("\n"),
        ),
      ),
    },
  });

  test("Find reveals a result above the pinned header", async ({ page }) => {
    await page.locator(".editor-empty-review").click();
    const section = page.locator(".unified-review-file", {
      has: page.locator(".unified-review-file-name", { hasText: "a-small.txt" }),
    });
    await expect(section.locator(".weavie-inline-newfile-tag")).toHaveText("New file");
    const first = section.locator(".view-line", { hasText: /^Small\sline\s01$/ });
    await first.hover();
    for (let notch = 0; notch < 6; notch++) await page.mouse.wheel(0, 120);
    await section.locator(".view-line", { hasText: /^Small\sline\s15$/ }).click();
    await page.keyboard.press("ControlOrMeta+f");
    await section.locator(".find-widget textarea").first().fill("Small line 01");
    await page.keyboard.press("Enter");
    await page.keyboard.press("Escape");
    await expect(first).toBeInViewport();
    const header = await section.locator(".unified-review-file-header").boundingBox();
    const line = await first.boundingBox();
    if (header === null || line === null) throw new Error("Find result is missing");
    expect(line.y).toBeGreaterThanOrEqual(header.y + header.height);
  });
});
