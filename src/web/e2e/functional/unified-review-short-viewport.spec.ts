import { awaitFontsSettled, pressDocumentEnd, pressDocumentStart } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { awaitReviewSet } from "../harness/navigator";
import { appliedEdit } from "../harness/review";
import { reviewScroll } from "../harness/review-scroll";

const paths = ["a-short.txt", "b-short.txt", "c-short.txt"] as const;
const rowCount = 20;
test.use({
  fakeScript: {
    steps: paths.flatMap((path) =>
      appliedEdit(
        path,
        Array.from({ length: rowCount }, (_, index) => `${path} row ${index + 1}`).join("\n"),
      ),
    ),
  },
});

test("short editors retain their height across file boundaries and reveal keyboard navigation", async ({
  page,
}) => {
  await page.setViewportSize({ width: 1400, height: 850 });
  await awaitReviewSet(page, [...paths]);
  await page.locator(".editor-empty-review").click();
  const viewport = page.locator(".unified-review-diffs > .monaco-scrollable-element");
  const section = page.locator(".unified-review-file", {
    has: page.locator(".unified-review-file-name", { hasText: paths[1] }),
  });
  await expect(
    page.locator(".unified-review-file").first().locator(".weavie-inline-newfile-tag"),
  ).toHaveText("New file");
  await expect(section.locator(".weavie-inline-newfile-tag")).toHaveText("New file");
  await awaitFontsSettled(page);
  const editor = section.locator(".monaco-editor");
  const firstLine = section.locator(".view-line", { hasText: /^b-short.txt\srow\s1$/ });
  const lastLine = section.locator(".view-line", {
    hasText: new RegExp(`^b-short.txt\\srow\\s${rowCount}$`),
  });
  await expect(firstLine).toBeAttached();
  const editorHeight = await editor.evaluate((element) => element.clientHeight);
  const view = await viewport.boundingBox();
  const first = await firstLine.boundingBox();
  if (view === null || first === null) throw new Error("Review content is missing");
  expect(editorHeight).toBeLessThan(view.height);

  const target = (await reviewScroll(page)).top + first.y - (view.y + view.height - 150);
  expect(target).toBeGreaterThanOrEqual(0);
  const initialTop = (await reviewScroll(page)).top;
  const steps = Math.ceil((target - initialTop) / 40);
  for (let step = 0; step < steps; step++)
    await page.getByRole("scrollbar", { name: "Review scroll position" }).press("ArrowDown");
  await expect(firstLine).toBeInViewport();
  await expect(lastLine).not.toBeInViewport();
  expect(await editor.evaluate((element) => element.clientHeight)).toBe(editorHeight);

  const expectCursorVisible = async (row: number): Promise<void> => {
    const line = section.locator(".view-line", {
      hasText: new RegExp(`^b-short.txt\\srow\\s${row}$`),
    });
    await expect
      .poll(() =>
        line.evaluate((element) => {
          const bounds = element.getBoundingClientRect();
          const header = element
            .closest(".unified-review-file")!
            .querySelector(".unified-review-file-header")!
            .getBoundingClientRect();
          const toolbar = document.querySelector(".weavie-inline-toolbar")!.getBoundingClientRect();
          return Math.min(bounds.top - header.bottom, toolbar.top - bounds.bottom);
        }),
      )
      .toBeGreaterThanOrEqual(0);
    expect(await editor.evaluate((element) => element.clientHeight)).toBe(editorHeight);
  };

  await firstLine.click({ position: { x: 10, y: 10 } });
  await pressDocumentEnd(page);
  await expectCursorVisible(rowCount);
  await pressDocumentStart(page);
  await expectCursorVisible(1);
  for (let row = 2; row <= rowCount; row++) {
    await page.keyboard.press("ArrowDown");
    await expectCursorVisible(row);
  }

  await firstLine.hover({ position: { x: 10, y: 10 } });
  const scroll = await reviewScroll(page);
  const step = Math.min(150, Math.floor((scroll.maximum - scroll.top) / 2));
  expect(step).toBeGreaterThan(0);
  for (const delta of [step, step, -step, -step]) {
    const before = (await reviewScroll(page)).top;
    await page.mouse.wheel(0, delta);
    await expect
      .poll(async () => ((await reviewScroll(page)).top - before) * Math.sign(delta))
      .toBeGreaterThan(0);
    expect(await editor.evaluate((element) => element.clientHeight)).toBe(editorHeight);
  }
});
