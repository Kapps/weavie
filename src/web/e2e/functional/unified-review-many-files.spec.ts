import { execFileSync } from "node:child_process";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { expect, test } from "../harness/fixtures";

// The file map draws one segment per file; with hundreds of files it once outgrew the pane, widening the review's
// column so the toolbar centred off-screen and every row's right edge was clipped.
test("a review of hundreds of files stays as wide as its pane", async ({ page, weavie }) => {
  const git = (...args: string[]) =>
    execFileSync("git", ["-c", "commit.gpgsign=false", ...args], { cwd: weavie.workspace });
  git("checkout", "-q", "-b", "feature/many-files");
  mkdirSync(join(weavie.workspace, "many"));
  for (let i = 0; i < 300; i++)
    writeFileSync(join(weavie.workspace, `many/file-${i}.txt`), `${i}\n`);
  git("add", "-A");
  git("commit", "-q", "-m", "Add many files");

  await page.getByRole("button", { name: /^Review Branch\b/ }).click();
  const toolbar = page.locator(".unified-review-controls > .weavie-inline-toolbar");
  await expect(toolbar).toBeVisible();
  await expect(page.locator(".unified-review-map-file")).toHaveCount(300);
  const geometry = await page.evaluate(() => {
    const box = (selector: string) => document.querySelector(selector)!.getBoundingClientRect();
    const review = box(".unified-review");
    const bar = box(".unified-review-controls > .weavie-inline-toolbar");
    const files = document.querySelectorAll(".unified-review-map-file");
    return {
      overflow: document.querySelector(".unified-review")!.scrollWidth - review.width,
      offCentre: Math.abs(bar.left + bar.width / 2 - (review.left + review.width / 2)),
      mapEnd: files[files.length - 1]!.getBoundingClientRect().right - review.right,
    };
  });
  expect(geometry.overflow).toBeLessThanOrEqual(1);
  expect(geometry.offCentre).toBeLessThanOrEqual(1);
  expect(geometry.mapEnd).toBeLessThanOrEqual(1);
});
