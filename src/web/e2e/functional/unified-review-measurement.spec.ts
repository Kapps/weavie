import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";
import { reviewEditor, reviewPaint } from "../harness/review-renderer";
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
  await reviewPaint(page)
    .first()
    .locator(".view-line")
    .first()
    .click({ position: { x: 5, y: 5 } });
  await expect(reviewEditor(page)).toBeVisible();
  expect(await observation.evaluate((sample) => sample.finish())).toBe(0);
});

test("retained sections reuse measured heights without synchronous reads during return scrolling", async ({
  page,
}) => {
  await expect(page.locator(".editor-empty-review")).toContainText("3");
  await page.locator(".editor-empty-review").click();
  const first = page.locator('.unified-review-file[data-index="1"]');
  const last = page.locator('.unified-review-file[data-index="3"]');
  await expect(reviewPaint(first).locator(".view-line").first()).toBeInViewport();
  const original = await first.elementHandle();
  const maximum = await page
    .getByRole("scrollbar", { name: "Review scroll position" })
    .getAttribute("aria-valuemax");
  await scrollReview(page, "end");
  await expect(last).toBeInViewport();
  await expect(first).not.toBeInViewport();
  expect(await original!.evaluate((element) => element.isConnected)).toBe(true);
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
  await expect(reviewPaint(first).locator(".view-line").first()).toBeInViewport();
  expect(await observation.evaluate((sample) => sample.finish())).toBe(0);
  await expect(page.getByRole("scrollbar", { name: "Review scroll position" })).toHaveAttribute(
    "aria-valuemax",
    maximum!,
  );
  await expect(reviewEditor(page)).toHaveCount(0);
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

  test("wrapped files attach at their final width on initial and repeated activation", async ({
    page,
  }) => {
    await expect(page.locator(".editor-empty-review")).toContainText("3");
    const observation = await page.evaluateHandle(() => {
      const monaco = window.__WEAVIE_MONACO__!;
      const samples: { path: string; width: number; wrappingColumn: number }[] = [];
      const subscription = monaco.editor.onDidCreateEditor((editor) => {
        const changed = editor.onDidChangeModel(() => {
          const model = editor.getModel();
          if (model === null) return;
          samples.push({
            path: model.uri.path,
            width: editor.getLayoutInfo().width,
            wrappingColumn: editor.getOption(monaco.editor.EditorOption.wrappingInfo)
              .wrappingColumn,
          });
        });
        editor.onDidDispose(() => changed.dispose());
      });
      return {
        finish: () => {
          subscription.dispose();
          return samples;
        },
      };
    });
    await page.locator(".editor-empty-review").click();
    const first = page.locator('.unified-review-file[data-index="1"]');
    const lines = reviewPaint(first).locator(".view-line");
    await expect(lines.first()).toBeVisible();
    await expect(first.locator(".unified-review-notice")).toHaveCount(0);
    const height = await first.evaluate((element) => element.clientHeight);
    expect(height).toBeGreaterThan(2_000);
    const width = await first
      .locator(".review-adaptive-body")
      .evaluate((element) => element.clientWidth);
    expect(width).toBeGreaterThan(100);
    await expect(lines.nth(1)).toContainText("wrapped");
    await lines.first().click({ position: { x: 5, y: 5 } });
    await expect(reviewEditor(first)).toBeVisible();
    await page.locator(".unified-review-tree-row.file").last().click();
    await expect(
      reviewEditor(page.locator('.unified-review-file[data-index="3"]'))
        .locator(".view-line")
        .first(),
    ).toBeInViewport();
    await scrollReview(page, "end");
    await expect(first).not.toBeInViewport();
    await expect(reviewEditor(first)).toHaveCount(0);
    await scrollReview(page, "start");
    await expect(lines.first()).toBeVisible();
    await lines.first().click({ position: { x: 5, y: 5 } });
    await expect(reviewEditor(first)).toBeVisible();
    await expect.poll(() => first.evaluate((element) => element.clientHeight)).toBe(height);
    expect(await reviewEditor(first).evaluate((element) => element.clientWidth)).toBe(width);
    const samples = await observation.evaluate((sample) => sample.finish());
    expect(samples.filter((sample) => sample.path.endsWith("/a.txt"))).toHaveLength(2);
    for (const sample of samples) {
      expect(sample.width).toBe(width);
      expect(sample.wrappingColumn).toBeGreaterThan(1);
    }
  });
});
