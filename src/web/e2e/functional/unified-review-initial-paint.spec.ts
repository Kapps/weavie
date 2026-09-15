import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";

const baseline = Array.from({ length: 7_000 }, (_, index) => `line ${index}`).join("\n");
const workerRequested = Promise.withResolvers<void>();
const releaseWorker = Promise.withResolvers<void>();

test.use({
  workspaceSeed: {
    run: async (workspace) => {
      await writeFile(join(workspace, "large-source.txt"), baseline);
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
    steps: appliedEdit("large-source.txt", baseline.replace("line 3500", "changed line 3500")),
  },
});

test("a pending diff never sizes a small change to its entire 7,000-line source", async ({
  page,
}) => {
  await page.locator(".editor-empty-review").click();
  const section = page.locator(".unified-review-file");
  await expect(section.locator(".monaco-editor")).toBeVisible();
  await workerRequested.promise;
  try {
    const viewport = await page.locator(".unified-review-diffs").evaluate((el) => el.clientHeight);
    expect(await section.evaluate((el) => el.clientHeight)).toBeLessThan(viewport);
  } finally {
    releaseWorker.resolve();
  }
  await expect(section.locator(".view-line", { hasText: "changed line 3500" })).toBeVisible();
  await expect(section.locator(".weavie-inline-removed-content")).toContainText("line 3500");
});
