import { openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";

// Regression (#137): the Diff-category commands (Next/Previous Change, Keep/Revert Change, Undo All) declined
// silently with no active diff, yet sorted to the TOP of the empty-workspace palette — a no-op-on-click first
// impression. They now carry a command-level `diffActive` `when` guard the inline-diff controller sets, so the
// palette lists them only when they would act. Pure-frontend `when`-evaluation through the real stack → headless.

const TWO_HUNKS =
  "export function greet(name: string): string {\n" +
  "  return `Hi there, ${name}!`;\n" +
  "}\n\n" +
  'const message = greet("weavie");\n' +
  "console.warn(message);\n";

const DIFF_TITLES = [
  "Next Change",
  "Previous Change",
  "Keep Change",
  "Revert Change",
  "Undo All Changes",
];

// A Diff-category row for a given command title — scoped by the "Diff" category chip so a fuzzy filename match
// can't be mistaken for it.
const diffRow = (page: import("@playwright/test").Page, title: string) =>
  page
    .locator(".tb-omnibar-row", { hasText: title })
    .filter({ has: page.locator(".tb-row-dir", { hasText: "Diff" }) });

async function openPalette(page: import("@playwright/test").Page, query: string): Promise<void> {
  const box = page.locator(".tb-omnibar-box");
  await page.keyboard.press("Escape");
  await expect(box).not.toHaveClass(/\bopen\b/);
  await expect(async () => {
    await page.keyboard.press("ControlOrMeta+Shift+p");
    await expect(box).toHaveClass(/\bopen\b/, { timeout: 1000 });
  }).toPass({ timeout: 10_000 });
  await page.locator(".tb-omnibar-input").fill(query);
}

test.describe("diff palette gating — no diff active", () => {
  test("Diff commands are absent from the empty-workspace palette", async ({ page }) => {
    // No file open, no diff → `diffActive` is falsy.
    await openPalette(page, ">change");
    // Some non-diff row matches ">change" so the palette is populated, but no Diff-category command appears.
    for (const title of DIFF_TITLES) {
      await expect(diffRow(page, title)).toHaveCount(0);
    }

    // Same under the ">diff" query (the other phrasing a user would try).
    await openPalette(page, ">diff");
    for (const title of DIFF_TITLES) {
      await expect(diffRow(page, title)).toHaveCount(0);
    }
  });
});

test.describe("diff palette gating — diff active", () => {
  test.use({ fakeScript: { steps: [...appliedEdit("hello.ts", TWO_HUNKS)] } });

  test("Diff commands appear once a review is active, and vanish again when it's cleared", async ({
    page,
  }) => {
    // Open the changed file: the applied review renders an inline diff over it → `diffActive` true.
    await openFile(page, "hello.ts");
    await expect(page.locator(".weavie-inline-added").first()).toBeVisible({ timeout: 15_000 });

    await openPalette(page, ">change");
    for (const title of DIFF_TITLES) {
      await expect(diffRow(page, title)).toHaveCount(1);
    }

    // Commit the whole set with Keep All (a non-gated command) — the review surface clears, `diffActive` goes
    // false, and the Diff commands drop back out of the palette.
    await openPalette(page, ">Keep All Changes");
    await page.locator(".tb-omnibar-input").press("Enter");
    await expect(page.locator(".weavie-inline-toolbar")).toHaveCount(0);

    await openPalette(page, ">change");
    for (const title of DIFF_TITLES) {
      await expect(diffRow(page, title)).toHaveCount(0);
    }
  });
});
