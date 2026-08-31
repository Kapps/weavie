import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";

// #125: when a turn lands changes but NO file is open, the inline parked navigator can't render (it lives in
// the editor that isn't mounted at boot), so the editor empty-state pane is the only place the user can learn
// Claude changed files. It must surface a "Review changes — N files" cue, and clicking it must open the
// unified overview. Boot leaves no file open, so the empty-state pane is showing.

const TWO_HUNKS =
  "export function greet(name: string): string {\n" +
  "  return `Hi there, ${name}!`;\n" +
  "}\n\n" +
  'const message = greet("weavie");\n' +
  "console.warn(message);\n";

test.describe("empty-state review cue (#125)", () => {
  test.use({
    fakeScript: {
      steps: [
        ...appliedEdit("hello.ts", TWO_HUNKS),
        ...appliedEdit("notes.txt", "just plain text\nand a second changed line\n"),
      ],
    },
  });

  test("no file open: the cue counts the changed files and opens unified review", async ({
    page,
  }) => {
    // Boot leaves no editor tab open, so the empty-state pane is showing — and the inline parked navigator
    // (which needs a mounted editor) never appears. Guard the precondition.
    await expect(page.locator(".editor-empty")).toBeVisible();
    await expect(page.locator(".editor-tab")).toHaveCount(0);

    // The turn landed a two-file review; the cue must surface it on the empty pane with the right count.
    const cue = page.locator(".editor-empty-review");
    await expect(cue).toBeVisible({ timeout: 15_000 });
    await expect(cue).toContainText("Review changes — 2 files");

    // Clicking it opens the all-files overview without manufacturing an editor tab.
    await cue.click();
    await expect(page.locator(".unified-review")).toBeVisible({ timeout: 15_000 });
    await expect(page.locator(".unified-review-file")).toHaveCount(2);
    await expect(page.locator(".editor-tab")).toHaveCount(0);
  });
});

test.describe("empty-state review cue — singular count (#125)", () => {
  test.use({ fakeScript: { steps: [...appliedEdit("hello.ts", TWO_HUNKS)] } });

  test("a single changed file reads 'Review changes — 1 file' (no plural s)", async ({ page }) => {
    await expect(page.locator(".editor-empty")).toBeVisible();
    const cue = page.locator(".editor-empty-review");
    await expect(cue).toBeVisible({ timeout: 15_000 });
    await expect(cue).toContainText("Review changes — 1 file");
    await expect(cue).not.toContainText("1 files");
  });
});
