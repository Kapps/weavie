import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { awaitReviewSet, navChord } from "../harness/navigator";
import { appliedEdit } from "../harness/review";

const baseline = Array.from({ length: 50 }, (_, index) => `line ${index + 1}`);
const changed = baseline.map((line, index) =>
  index === 1 || index === 39 ? `${line} changed` : line,
);
const content = `${changed.join("\n")}\n`;

test.use({
  fakeScript: {
    steps: [
      { op: "edit", path: "{{WORKSPACE}}/review.txt", content: `${baseline.join("\n")}\n` },
      ...appliedEdit("review.txt", content),
      ...appliedEdit("notes.txt", "a changed note\n"),
    ],
  },
});

test("the existing toolbar and keyboard review the active unified section without opening a file", async ({
  page,
  weavie,
}) => {
  await awaitReviewSet(page, ["notes.txt", "review.txt"]);
  await openFile(page, "notes.txt");
  await page.locator(".editor-review-toggle").click();
  const overview = page.locator(".unified-review");
  const toolbar = overview.locator(".weavie-inline-toolbar");
  const name = toolbar.locator(".weavie-inline-stack-name");
  const counter = toolbar.locator(".weavie-inline-stack-sub");
  await expect(toolbar).toBeVisible();
  await expect(page.locator(".weavie-inline-toolbar")).toHaveCount(1);
  await expect(
    overview.locator(".unified-review-action", { hasText: /Previous|Next/ }),
  ).toHaveCount(0);

  await toolbar.locator("button[title^='Next file']").click();
  await expect(name).toHaveText("review.txt");
  await expect(counter).toContainText("change 1/2");
  await toolbar.locator("button[title^='Next change']").click();
  await expect(counter).toContainText("change 2/2");
  await page.keyboard.press(navChord("ArrowUp"));
  await expect(counter).toContainText("change 1/2");
  await expect(overview).toBeVisible();
  await expect(page.locator(".editor-tab.active")).toContainText("notes.txt");

  await toolbar.locator(".weavie-inline-accept").click();
  await expect(counter).toContainText("change 1/1");
  const review = overview.locator(".unified-review-file", {
    has: page.locator(".unified-review-file-name", { hasText: "review.txt" }),
  });
  await expect(review.locator(".weavie-inline-accepted")).toHaveCount(1);
  await page.keyboard.press("ControlOrMeta+Backspace");
  await expect
    .poll(() => readFile(join(weavie.workspace, "review.txt"), "utf8"))
    .toBe(
      `${baseline.map((line, index) => (index === 1 ? `${line} changed` : line)).join("\n")}\n`,
    );
  await expect(overview).toBeVisible();
  await expect(name).toHaveText("notes.txt");
  await expect
    .poll(() => readFile(join(weavie.workspace, "notes.txt"), "utf8"))
    .toBe("a changed note\n");
  await page.locator(".editor-review-toggle").click();
  await expect(overview).toHaveCount(0);
  await expect(page.locator(".editor-tab.active")).toContainText("notes.txt");
});

test("file navigation wraps and file-scope Keep uses the unified selection", async ({ page }) => {
  await awaitReviewSet(page, ["notes.txt", "review.txt"]);
  await openFile(page, "notes.txt");
  await page.locator(".editor-review-toggle").click();
  const overview = page.locator(".unified-review");
  const toolbar = overview.locator(".weavie-inline-toolbar");
  const name = toolbar.locator(".weavie-inline-stack-name");
  await expect(name).toHaveText("notes.txt");
  await page.keyboard.press(navChord("ArrowLeft"));
  await expect(name).toHaveText("review.txt");
  await toolbar.locator("button[title^='Previous file']").click();
  await expect(name).toHaveText("notes.txt");
  await page.keyboard.press(navChord("ArrowRight"));
  await expect(name).toHaveText("review.txt");
  await toolbar.locator(".weavie-inline-scope-btn").click();
  await toolbar.locator(".weavie-inline-scope-item", { hasText: "This file" }).click();
  await page.keyboard.press("ControlOrMeta+Enter");
  await expect(overview.locator(".unified-review-file", { hasText: "review.txt" })).toContainText(
    "Reviewed",
  );
  await expect(page.locator(".weavie-inline-toolbar")).toBeVisible();
  await page.keyboard.press(navChord("ArrowRight"));
  await expect(name).toHaveText("notes.txt");
  await expect(toolbar.locator(".weavie-inline-scope-btn")).toContainText("File");
  await expect(overview).toBeVisible();
  await expect(
    overview
      .locator(".unified-review-file", { hasText: "notes.txt" })
      .locator(".unified-review-file-action.keep"),
  ).toBeVisible();
});

test("collapsing the selected file keeps navigation available and reopens it on Next Change", async ({
  page,
}) => {
  await awaitReviewSet(page, ["notes.txt", "review.txt"]);
  await page.locator(".editor-empty-review").click();
  const overview = page.locator(".unified-review");
  const review = overview.locator(".unified-review-file", { hasText: "review.txt" });
  await overview.locator(".unified-review-tree-row.file", { hasText: "review.txt" }).click();
  await expect(overview.locator(".weavie-inline-stack-name")).toHaveText("review.txt");
  await review.locator(".unified-review-file-toggle").click();
  await expect(review.locator(".unified-review-file-toggle")).toHaveAttribute(
    "aria-expanded",
    "false",
  );
  const toolbar = page.locator(".weavie-inline-toolbar");
  await expect(toolbar).toBeVisible();
  await toolbar.locator(".weavie-inline-nav").nth(1).click();
  await expect(review.locator(".unified-review-file-toggle")).toHaveAttribute(
    "aria-expanded",
    "true",
  );
  await expect(overview.locator(".weavie-inline-stack-name")).toHaveText("review.txt");
  await expect(overview).toBeVisible();
});

test("mode toggles restore the exact hunk and history actions update the shared toolbar", async ({
  page,
}) => {
  await awaitReviewSet(page, ["notes.txt", "review.txt"]);
  await page.locator(".editor-empty-review").click();
  const overview = page.locator(".unified-review");
  await overview.locator(".unified-review-tree-row.file", { hasText: "review.txt" }).click();
  const toolbar = overview.locator(".weavie-inline-toolbar");
  const counter = toolbar.locator(".weavie-inline-stack-sub");
  await toolbar.locator("button[title^='Next change']").click();
  await expect(counter).toContainText("change 2/2");
  await page.locator(".editor-review-toggle").click();
  await expect(page.locator(".weavie-inline-stack-sub")).toContainText("change 2/2");
  await page.locator(".editor-review-toggle").click();
  await expect(counter).toContainText("change 2/2");
  await toolbar.locator(".weavie-inline-accept").click();
  await expect(counter).toContainText("change 1/1");
  const history = toolbar.locator(".weavie-inline-hist");
  await expect(history.first()).toBeEnabled();
  await history.first().click();
  await expect(counter).toContainText("change 2/2");
  await expect(history.nth(1)).toBeEnabled();
  await history.nth(1).click();
  await expect(counter).toContainText("change 1/1");
  await expect(history.first()).toBeEnabled();
  await page.keyboard.press("ControlOrMeta+Shift+Enter");
  await expect(counter).toContainText("change 2/2");
  await expect(overview).toBeVisible();
});
