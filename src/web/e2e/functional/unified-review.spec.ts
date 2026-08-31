import { openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";

const HELLO =
  "export function greet(name: string): string {\n" +
  "  return `Hello from unified review, ${name}!`;\n" +
  "}\n\n" +
  'const message = greet("weavie");\n' +
  "console.warn(message);\n";

test.describe("unified review mode", () => {
  test.use({
    fakeScript: {
      steps: [
        ...appliedEdit("hello.ts", HELLO),
        ...appliedEdit("notes.txt", "just plain text\na unified addition\n"),
      ],
    },
  });

  test("summarizes every file and toggles into the exact file review", async ({ page }) => {
    const cue = page.locator(".editor-empty-review");
    await expect(cue).toBeVisible({ timeout: 15_000 });
    await cue.click();

    const overview = page.locator(".unified-review");
    await expect(overview).toBeVisible();
    await expect(overview.locator(".unified-review-heading")).toContainText("2 files changed");
    await expect(overview.locator(".unified-review-file-link")).toHaveCount(2);
    await expect(overview.locator(".unified-review-file")).toHaveCount(2);
    await expect(overview.locator(".unified-review-row.added").first()).toBeVisible({
      timeout: 15_000,
    });
    await expect(overview.locator(".unified-review-notice", { hasText: "Loading" })).toHaveCount(0);

    await overview.locator(".unified-review-file-name", { hasText: "hello.ts" }).click();
    await expect(overview).toHaveCount(0);
    await expect(page.locator(".editor-tab", { hasText: "hello.ts" })).toBeVisible();
    await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 15_000 });

    const mode = page.locator(".editor-review-toggle");
    await expect(mode).toHaveText("All changes");
    await mode.click();
    await expect(overview).toBeVisible();
    await expect(mode).toHaveText("File review");

    await openFile(page, "README.md");
    await expect(overview).toHaveCount(0);
    await expect(page.locator(".editor-tab.active", { hasText: "README.md" })).toBeVisible();
  });

  test("a file-level keep updates the overview to a reviewed band", async ({ page }) => {
    await page.locator(".editor-empty-review").click();
    const notes = page.locator(".unified-review-file", { hasText: "notes.txt" });
    await expect(notes.locator(".unified-review-file-action.keep")).toBeVisible({
      timeout: 15_000,
    });

    await notes.locator(".unified-review-file-action.keep").click();

    await expect(notes.locator(".unified-review-status")).toHaveText("Reviewed", {
      timeout: 15_000,
    });
    await expect(notes.locator(".unified-review-patch.reviewed")).toBeVisible();
    await expect(notes.locator(".unified-review-file-action.keep")).toHaveCount(0);
  });
});

test.describe("unified review mode — large file", () => {
  test.use({
    fakeScript: {
      steps: [
        ...appliedEdit(
          "large-review.txt",
          Array.from({ length: 4_000 }, (_, index) => `line ${index}`).join("\n"),
        ),
      ],
    },
  });

  test("renders a 4,000-line change without a presentation cutoff", async ({ page }) => {
    await page.locator(".editor-empty-review").click();
    const overview = page.locator(".unified-review");
    await expect(overview.locator(".unified-review-hunk")).toBeVisible({ timeout: 15_000 });
    await expect(overview).not.toContainText("Diff calculation timed out");
    await expect(overview.locator(".unified-review-row", { hasText: "line 3999" })).toHaveCount(1);
  });
});

test.describe("unified review mode — large file set", () => {
  const fileCount = 100;
  test.use({
    fakeScript: {
      steps: Array.from({ length: fileCount }, (_, index) =>
        appliedEdit(`review-${String(index).padStart(3, "0")}.txt`, `change ${index}\n`),
      ).flat(),
    },
  });

  test("restores the exact file across a reverse mode toggle without mounting every diff", async ({
    page,
  }) => {
    await page.locator(".editor-empty-review").click();
    const overview = page.locator(".unified-review");
    const targetName = "review-099.txt";
    const targetLink = overview.locator(".unified-review-file-link", { hasText: targetName });
    await expect(overview.locator(".unified-review-file-link")).toHaveCount(fileCount);

    await targetLink.click();
    const targetSection = overview.locator(".unified-review-file", { hasText: targetName });
    await expect(targetSection).toBeVisible({ timeout: 15_000 });
    await expect.poll(() => overview.locator(".unified-review-file").count()).toBeLessThan(20);
    await targetSection.locator(".unified-review-file-name").click();
    await expect(page.locator(".editor-tab.active", { hasText: targetName })).toBeVisible();

    await page.locator(".editor-review-toggle").click();
    await expect(overview).toBeVisible();
    await expect(targetLink).toHaveClass(/active/);
    await expect(targetSection).toBeVisible();

    await overview.locator(".unified-review-action.mode").click();
    await expect(page.locator(".editor-tab.active", { hasText: targetName })).toBeVisible();
  });
});
