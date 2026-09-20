import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";
import { scrollReview } from "../harness/review-scroll";

test.use({
  fakeScript: {
    steps: ["a", "b", "c"].flatMap((name) =>
      appliedEdit(
        `${name}.txt`,
        Array.from({ length: 500 }, (_, index) => `${name} line ${index}`).join("\n"),
      ),
    ),
  },
});

test("the review viewport owns the embedded editor's initial size", async ({ page }) => {
  await expect(page.locator(".editor-empty-review")).toContainText("3");
  const observation = await page.evaluateHandle(() => {
    const width = Object.getOwnPropertyDescriptor(Element.prototype, "clientWidth")!;
    let reads = 0;
    Object.defineProperty(Element.prototype, "clientWidth", {
      ...width,
      get() {
        if (this.classList.contains("unified-review-editor-viewport")) reads++;
        return width.get!.call(this);
      },
    });
    return {
      finish: () => {
        Object.defineProperty(Element.prototype, "clientWidth", width);
        return reads;
      },
    };
  });
  await page.locator(".editor-empty-review").click();
  await expect(page.locator(".unified-review-file .view-line").first()).toBeVisible();
  expect(await observation.evaluate((sample) => sample.finish())).toBe(0);
});

test("remounted sections reuse their measured height until the resize observer reports a change", async ({
  page,
}) => {
  await expect(page.locator(".editor-empty-review")).toContainText("3");
  await page.locator(".editor-empty-review").click();
  const first = page.locator('.unified-review-file[data-index="1"] .monaco-editor');
  const last = page.locator('.unified-review-file[data-index="3"] .monaco-editor');
  await expect(first).toBeVisible();
  await scrollReview(page, "end");
  await expect(last).toBeVisible();
  await expect(first).toHaveCount(0);
  const observation = await page.evaluateHandle(() => {
    const rect = Element.prototype.getBoundingClientRect;
    const height = Object.getOwnPropertyDescriptor(HTMLElement.prototype, "offsetHeight")!;
    let reads = 0;
    Element.prototype.getBoundingClientRect = function () {
      if (this.classList.contains("unified-review-file")) reads++;
      return rect.call(this);
    };
    Object.defineProperty(HTMLElement.prototype, "offsetHeight", {
      ...height,
      get() {
        if (this.classList.contains("unified-review-file")) reads++;
        return height.get!.call(this);
      },
    });
    return {
      finish: () => {
        Element.prototype.getBoundingClientRect = rect;
        Object.defineProperty(HTMLElement.prototype, "offsetHeight", height);
        return reads;
      },
    };
  });
  await scrollReview(page, "start");
  await expect(first).toBeVisible();
  expect(await observation.evaluate((sample) => sample.finish())).toBe(0);
});

test.describe("wrapped review construction", () => {
  test.use({
    fakeScript: {
      steps: [
        { op: "mcp", tool: "setSetting", args: { key: "editor.wordWrap", value: "on" } },
        ...["a", "b", "c"].flatMap((name) =>
          appliedEdit(
            `${name}.txt`,
            Array.from({ length: 20 }, (_, index) =>
              `${name} line ${index} ${"wrapped text ".repeat(60)}`.trim(),
            ).join("\n"),
          ),
        ),
      ],
    },
  });

  test("wrapped files have their final width and height on initial and cached mounts", async ({
    page,
  }) => {
    await expect(page.locator(".editor-empty-review")).toContainText("3");
    await page.locator(".editor-empty-review").click();
    const first = page.locator('.unified-review-file[data-index="1"]');
    const lines = first.locator(".view-line");
    await expect(lines.first()).toBeVisible();
    await expect(first.locator(".unified-review-notice")).toHaveCount(0);
    const height = await first.evaluate((element) => element.clientHeight);
    expect(height).toBeGreaterThan(2_000);
    const width = await first.locator(".monaco-editor").evaluate((element) => element.clientWidth);
    expect(width).toBeGreaterThan(100);
    await expect(lines.nth(1)).toContainText("wrapped");
    await page.locator(".unified-review-tree-row.file").last().click();
    await expect(
      page.locator('.unified-review-file[data-index="3"] .view-line').first(),
    ).toBeInViewport();
    await expect(first).toHaveCount(0);
    await scrollReview(page, "start");
    await expect(lines.first()).toBeVisible();
    await expect.poll(() => first.evaluate((element) => element.clientHeight)).toBe(height);
    expect(await first.locator(".monaco-editor").evaluate((element) => element.clientWidth)).toBe(
      width,
    );
  });
});
