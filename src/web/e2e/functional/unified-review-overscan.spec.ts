import type { Page } from "@playwright/test";
import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";
import type { EditorHandle, WeavieWindow } from "../harness/weavie-window";

const fileName = "overscan.txt";
const content = Array.from({ length: 4_000 }, (_, index) => `new line ${index}`).join("\n");

test.use({ fakeScript: { steps: appliedEdit(fileName, content) } });

async function reviewState(page: Page) {
  return page.evaluate((name) => {
    const editors = (window as WeavieWindow).__WEAVIE_MONACO__?.editor.getEditors() as
      | EditorHandle[]
      | undefined;
    const editor = editors?.find(
      (candidate) =>
        candidate !== (window as WeavieWindow).__WEAVIE_EDITOR__ &&
        candidate.getModel()?.uri.path.endsWith(`/${name}`),
    );
    const scroller = document.querySelector<HTMLElement>(".unified-review-diffs");
    const header = document.querySelector(".unified-review-file-header");
    const position = editor?.getPosition();
    const cursor = position && editor?.getScrolledVisiblePosition(position);
    const editorBounds = editor?.getDomNode()?.getBoundingClientRect();
    if (!editor || !scroller || !header || !position || !cursor || !editorBounds) {
      throw new Error("Review editor is not mounted");
    }
    const viewport = scroller.getBoundingClientRect();
    const top = header.getBoundingClientRect().bottom;
    const bottom = viewport.bottom;
    const lines = [...document.querySelectorAll(".unified-review-file .view-line")].map(
      (element) => ({ text: element.textContent ?? "", bounds: element.getBoundingClientRect() }),
    );
    const visible = lines.filter((line) => line.bounds.top >= top && line.bounds.bottom <= bottom);
    const selection = editor.getSelections()?.[0] as {
      startLineNumber: number;
      endLineNumber: number;
    };
    return {
      line: position.lineNumber,
      selectedLines: selection.endLineNumber - selection.startLineNumber,
      cursorVisible:
        editorBounds.top + cursor.top >= top &&
        editorBounds.top + cursor.top + cursor.height <= bottom,
      visibleLines: Math.floor((bottom - top) / cursor.height),
      middleText: visible[Math.floor(visible.length / 2)]?.text.replace(/\u00a0/g, " "),
      renderedLines: lines.length,
      above: (top - Math.min(...lines.map((line) => line.bounds.top))) / (bottom - top),
      below: (Math.max(...lines.map((line) => line.bounds.bottom)) - bottom) / (bottom - top),
      scrollTop: scroller.scrollTop,
    };
  }, fileName);
}

async function expectOverscan(page: Page): Promise<void> {
  await expect.poll(async () => (await reviewState(page)).above).toBeGreaterThan(0.75);
  await expect.poll(async () => (await reviewState(page)).below).toBeGreaterThan(0.75);
  const state = await reviewState(page);
  expect(state.renderedLines).toBeLessThan(state.visibleLines * 4);
}

async function openReviewMiddle(page: Page) {
  await page.locator(".editor-empty-review").click();
  const scroller = page.locator(".unified-review-diffs");
  await expect(page.locator(".unified-review-file .view-line").first()).toBeVisible();
  await scroller.evaluate((element) => element.scrollTo(0, element.scrollHeight / 2));
  await expectOverscan(page);
  return scroller;
}

test("pre-renders code beyond both viewport edges while scrolling in either direction", async ({
  page,
}) => {
  const scroller = await openReviewMiddle(page);
  await scroller.hover();
  for (const delta of [900, -900]) {
    const before = (await reviewState(page)).scrollTop;
    await page.mouse.wheel(0, delta);
    await expect
      .poll(async () => ((await reviewState(page)).scrollTop - before) * Math.sign(delta))
      .toBeGreaterThan(500);
    await expectOverscan(page);
  }
});

test("keyboard navigation and page selections use the visible viewport inside the larger render window", async ({
  page,
}) => {
  await openReviewMiddle(page);
  const middleText = (await reviewState(page)).middleText;
  if (middleText === undefined) throw new Error("Review viewport has no visible code");
  await page.locator(".unified-review-file .view-line", { hasText: middleText }).click();

  const pageLines = (await reviewState(page)).visibleLines;
  for (const key of ["ArrowDown", "ArrowUp"]) {
    for (let index = 0; index < pageLines; index++) await page.keyboard.press(key);
    await expect.poll(async () => (await reviewState(page)).cursorVisible).toBe(true);
  }

  for (const key of ["PageDown", "PageUp"]) {
    const before = await reviewState(page);
    await page.keyboard.press(key);
    const after = await reviewState(page);
    const distance = Math.abs(after.line - before.line);
    expect(distance).toBeGreaterThanOrEqual(pageLines - 3);
    expect(distance).toBeLessThanOrEqual(pageLines);
    await expect.poll(async () => (await reviewState(page)).cursorVisible).toBe(true);
  }

  const selectionStart = (await reviewState(page)).line;
  await page.keyboard.press("Shift+PageDown");
  const selection = await reviewState(page);
  expect(selection.selectedLines).toBe(selection.line - selectionStart);
  expect(selection.selectedLines).toBeGreaterThanOrEqual(pageLines - 3);
  expect(selection.selectedLines).toBeLessThanOrEqual(pageLines);
  await expect.poll(async () => (await reviewState(page)).cursorVisible).toBe(true);
  await page.keyboard.press("Shift+PageUp");
  expect((await reviewState(page)).selectedLines).toBe(0);
  await expect.poll(async () => (await reviewState(page)).cursorVisible).toBe(true);

  for (const [key, line] of [
    ["ControlOrMeta+End", 4_000],
    ["ControlOrMeta+Home", 1],
  ] as const) {
    await page.keyboard.press(key);
    await expect.poll(async () => (await reviewState(page)).line).toBe(line);
    await expect.poll(async () => (await reviewState(page)).cursorVisible).toBe(true);
  }
});

test("drag selections scroll at the visible edges and restore overscan after release", async ({
  page,
}) => {
  const scroller = await openReviewMiddle(page);
  const bounds = await scroller.boundingBox();
  if (bounds === null) throw new Error("Review viewport is not mounted");

  for (const direction of [1, -1]) {
    const before = await reviewState(page);
    if (before.middleText === undefined) throw new Error("Review viewport has no visible code");
    const line = page.locator(".unified-review-file .view-line", { hasText: before.middleText });
    await line.hover();
    await page.mouse.down();
    await page.mouse.move(
      bounds.x + bounds.width / 2,
      direction > 0 ? bounds.y + bounds.height + 30 : bounds.y - 30,
      { steps: 6 },
    );
    await expect
      .poll(async () => ((await reviewState(page)).scrollTop - before.scrollTop) * direction)
      .toBeGreaterThan(bounds.height);
    await page.mouse.up();
    expect((await reviewState(page)).selectedLines).toBeGreaterThan(before.visibleLines);
    await expectOverscan(page);
  }
});

test("keeps Find controls visible and clickable while navigating beyond the viewport", async ({
  page,
}) => {
  const scroller = await openReviewMiddle(page);
  const middleText = (await reviewState(page)).middleText;
  if (middleText === undefined) throw new Error("Review viewport has no visible code");
  await page
    .locator(".unified-review-file .view-line", { hasText: middleText })
    .locator("span")
    .first()
    .click();
  await page.keyboard.press("ControlOrMeta+f");
  const find = page.locator(".unified-review-file .find-widget");
  const input = find.getByRole("textbox", { name: "Find", exact: true });
  const expectFindVisible = async (): Promise<void> => {
    await expect(input).toBeInViewport({ ratio: 1 });
    await expect
      .poll(async () => {
        const header = await page.locator(".unified-review-file-header").boundingBox();
        const field = await input.boundingBox();
        const viewport = await scroller.boundingBox();
        if (header === null || field === null || viewport === null)
          throw new Error("Find controls are not mounted");
        return Math.min(
          field.y - header.y - header.height,
          viewport.y + viewport.height - field.y - field.height,
        );
      })
      .toBeGreaterThanOrEqual(0);
  };
  await expectFindVisible();
  await input.fill("new line 4");
  await expect.poll(async () => (await reviewState(page)).line).toBe(5);
  await find.getByRole("button", { name: /^Next Match/ }).click();
  await expect.poll(async () => (await reviewState(page)).line).toBe(41);
  await expect.poll(async () => (await reviewState(page)).cursorVisible).toBe(true);
  await expectFindVisible();
});
