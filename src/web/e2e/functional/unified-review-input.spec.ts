import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";
import type { EditorHandle, WeavieWindow } from "../harness/weavie-window";

test.use({
  fakeScript: {
    steps: appliedEdit(
      "buffered-input.txt",
      Array.from({ length: 5_000 }, (_, index) => `Review line ${index + 1}`).join("\n"),
    ),
  },
});

test("buffered review preserves scroll-only commands and reveals distant find results", async ({
  page,
}) => {
  await page.locator(".editor-empty-review").click();
  const section = page.locator(".unified-review-file");
  const scroller = page.locator(".unified-review-diffs");
  await expect(section.locator(".weavie-inline-newfile-tag")).toHaveText("New file");
  await section.locator(".view-line", { hasText: /^Review\sline\s1$/ }).click();
  const editor = await page.evaluateHandle(() => {
    const editors = (
      window as WeavieWindow
    ).__WEAVIE_MONACO__?.editor.getEditors() as EditorHandle[];
    const review = editors.find((candidate) =>
      candidate.getModel()?.uri.path.endsWith("/buffered-input.txt"),
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
    const initialScroll = await scroller.evaluate((element) => element.scrollTop);
    await scroll("scrollLineDown");
    await expect
      .poll(() => scroller.evaluate((element) => element.scrollTop))
      .toBeGreaterThan(initialScroll);
    expect(await position()).toEqual(before);
    await scroll("scrollLineUp");
    await expect.poll(() => scroller.evaluate((element) => element.scrollTop)).toBe(initialScroll);
    expect(await position()).toEqual(before);
  };
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
});
