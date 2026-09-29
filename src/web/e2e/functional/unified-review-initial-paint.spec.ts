import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";
import { reviewPaint } from "../harness/review-renderer";
import { reviewScroll, scrollReview } from "../harness/review-scroll";

const baseline = Array.from({ length: 7_000 }, (_, index) => `line ${index}`).join("\n");
const paths = ["large-source.txt", "middle-source.txt", "z-source.txt"];
const workerRequested = Promise.withResolvers<void>();
const releaseWorker = Promise.withResolvers<void>();

test.use({
  workspaceSeed: {
    run: async (workspace) => {
      await Promise.all(paths.map((path) => writeFile(join(workspace, path), baseline)));
    },
  },
  preNavigate: {
    run: async (page) => {
      await page.route(/\/assets\/editor\.worker-[^/]+\.js$/, async (route) => {
        workerRequested.resolve();
        await releaseWorker.promise;
        await route.continue();
      });
    },
  },
  fakeScript: {
    steps: paths.flatMap((path) =>
      appliedEdit(path, baseline.replace("line 3500", "changed line 3500")),
    ),
  },
});

test("pending diffs remain bounded and scrollable until their geometry is ready", async ({
  page,
}) => {
  await expect(page.locator(".editor-empty-review")).toContainText("3");
  await page.locator(".editor-empty-review").click();
  const section = page.locator(".unified-review-file", {
    has: page.locator(".unified-review-file-name", { hasText: paths[0] }),
  });
  await expect(section.locator(".review-adaptive-body")).toBeAttached();
  await workerRequested.promise;
  try {
    await expect(section.locator(".review-adaptive-body")).toHaveAttribute("aria-busy", "true");
    await expect(section.locator(".review-adaptive-live .monaco-editor")).toHaveCount(0);
    await expect(section.locator(".unified-review-notice")).toHaveText("Preparing review…");
    const viewport = await page.locator(".unified-review-diffs").evaluate((el) => el.clientHeight);
    expect(await section.evaluate((el) => el.clientHeight)).toBeLessThan(viewport);
    await page.locator(".unified-review-diffs").hover();
    await page.mouse.wheel(0, 120);
    await expect.poll(() => reviewScroll(page).then(({ top }) => top)).toBeGreaterThan(0);
    await scrollReview(page, "end");
    await expect.poll(() => reviewScroll(page).then(({ top, maximum }) => maximum - top)).toBe(0);
    await expect(page.locator('.review-adaptive-body[aria-busy="true"]')).toHaveCount(3);
  } finally {
    releaseWorker.resolve();
  }
  await expect(section.locator(".review-adaptive-body")).toHaveAttribute("aria-busy", "false");
  await expect(page.locator('.review-adaptive-body[aria-busy="false"]')).toHaveCount(3);
  await scrollReview(page, "end");
  await expect(
    reviewPaint(page.locator(".unified-review-file").last()).locator(".view-line", {
      hasText: "changed line 3500",
    }),
  ).toBeInViewport();
  await scrollReview(page, "start");
  await expect(
    reviewPaint(section).locator(".view-line", { hasText: "changed line 3500" }),
  ).toBeVisible();
  await expect(
    section.locator(".weavie-inline-removed-line", { hasText: "line 3500" }),
  ).toBeVisible();
});
