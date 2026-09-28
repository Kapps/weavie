import { openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";
import { reviewScroll } from "../harness/review-scroll";

const paths = ["horizontal-a.txt", "horizontal-b.txt"];
test.use({
  fakeScript: {
    steps: paths.flatMap((path) =>
      appliedEdit(
        path,
        Array.from(
          { length: 60 },
          (_, line) => `${path} line ${line} ${"horizontal content ".repeat(80)}`,
        ).join("\n"),
      ),
    ),
  },
});

test("file horizontal position survives editing, resize, ownership transfer, and tab restore", async ({
  page,
}) => {
  await expect(page.locator(".editor-empty-review")).toContainText("2");
  await page.locator(".editor-empty-review").click();
  const first = page.locator('.unified-review-file[data-index="1"]');
  const body = first.locator(".review-adaptive-body");
  const horizontal = first.getByRole("scrollbar", { name: "File horizontal scroll position" });
  const vertical = page.getByRole("scrollbar", { name: "Review scroll position", exact: true });
  await expect(body).toHaveAttribute("aria-busy", "false");
  await expect(horizontal).toBeVisible();
  for (let index = 0; index < 4; index++) await horizontal.press("ArrowRight");
  await expect(horizontal).toHaveAttribute("aria-valuenow", "160");
  expect((await reviewScroll(page)).top).toBe(0);

  const activate = async (): Promise<void> => {
    await first.locator(".passive-review-body").click({ position: { x: 300, y: 15 } });
    await expect(body).toHaveAttribute("data-presentation", "live");
  };
  const liveState = (): Promise<{ left: number; width: number }> =>
    page.evaluate(() => {
      const monaco = (window as unknown as { __WEAVIE_MONACO__: typeof import("monaco-editor") })
        .__WEAVIE_MONACO__;
      const editors = monaco.editor
        .getEditors()
        .filter((candidate) => candidate.getModel()?.uri.path.endsWith("/horizontal-a.txt"));
      if (editors.length !== 1) throw new Error("Expected one live review editor");
      return { left: editors[0]!.getScrollLeft(), width: editors[0]!.getLayoutInfo().width };
    });

  await activate();
  await expect.poll(async () => (await liveState()).left).toBe(160);
  await vertical.press("End");
  await expect.poll(async () => (await liveState()).left).toBe(160);
  await page.setViewportSize({ width: 1600, height: 800 });
  await expect
    .poll(async () => (await liveState()).width)
    .toBe(await body.evaluate((element) => element.clientWidth));
  await expect.poll(async () => (await liveState()).left).toBe(160);
  const secondText = await page
    .locator('.unified-review-file[data-index="2"] .passive-review-body .view-line')
    .evaluateAll((lines) => {
      const viewport = document.querySelector(".unified-review-diffs")!.getBoundingClientRect();
      const target = lines
        .map((line) => line.getBoundingClientRect())
        .find((rect) => rect.top > viewport.top + 50 && rect.bottom < viewport.bottom - 50);
      if (!target) throw new Error("No second-file text visible for activation");
      return { x: target.left + 40, y: target.top + 8 };
    });
  await page.mouse.click(secondText.x, secondText.y);
  await expect(
    page.locator('.unified-review-file[data-index="2"] .review-adaptive-body'),
  ).toHaveAttribute("data-presentation", "live");
  await expect(body).toHaveAttribute("data-presentation", "passive");
  await expect(first.locator(".review-adaptive-live .monaco-editor")).toHaveCount(0);
  await vertical.press("Home");
  await expect(horizontal).toBeVisible();
  await expect(horizontal).toHaveAttribute("aria-valuenow", "160");

  await openFile(page, "README.md");
  await page.locator(".editor-tab", { hasText: "Review Changes" }).click();
  await expect(horizontal).toBeVisible();
  await expect(horizontal).toHaveAttribute("aria-valuenow", "160");
  await activate();
  await expect.poll(async () => (await liveState()).left).toBe(160);
});
