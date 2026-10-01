import type { Locator, Page } from "@playwright/test";
import { expectRevealed } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { awaitReviewSet } from "../harness/navigator";
import { appliedEdit } from "../harness/review";

const sourceName = "context-review.ts";
const baseline = Array.from({ length: 150 }, (_, index) => `export const line${index + 1} = 0;`);
// A change at line 40 and a deletion at 110–111 leave leading, middle, and trailing collapsed stretches.
const changed = baseline.map((line, index) => (index === 39 ? "export const line40 = 1;" : line));
changed.splice(109, 2);

test.use({
  fakeScript: {
    steps: [
      { op: "edit", path: `{{WORKSPACE}}/${sourceName}`, content: baseline.join("\n") },
      ...appliedEdit(sourceName, changed.join("\n")),
    ],
  },
});

async function openSection(page: Page): Promise<Locator> {
  await awaitReviewSet(page, [sourceName]);
  await page.locator(".editor-empty-review").click();
  const section = page.locator(".unified-review-file", {
    has: page.locator(".unified-review-file-name", { hasText: sourceName }),
  });
  await expect(section.locator(".unified-review-gap")).toHaveText([
    "Show 36 unchanged lines",
    "Show 63 unchanged lines",
    "Show 36 unchanged lines",
  ]);
  return section;
}

test("every collapsed stretch shows a band, and a real click on one reveals its lines", async ({
  page,
}) => {
  const section = await openSection(page);
  for (const band of await section.locator(".unified-review-gap").all()) {
    await expect(band).toBeVisible();
  }
  const middle = section.locator(".unified-review-gap", { hasText: "Show 63" });
  await expect(middle).toBeInViewport();
  // Monaco's text layer covers view zones, so click the point the way a user does.
  const box = (await middle.boundingBox())!;
  await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
  await expect(middle).toHaveCount(0);
  const opened = section.locator(".view-line", { hasText: "line44 = 0" });
  await expect(opened).toBeInViewport();
  expect(Math.abs((await opened.boundingBox())!.y - box.y)).toBeLessThan(2);
  await expect(section.locator(".unified-review-gap")).toHaveCount(2);
});

test("Alt+] shows the whole file and collapses it back", async ({ page }) => {
  const section = await openSection(page);
  await section.locator(".view-line", { hasText: "line38 = 0" }).click();
  await page.keyboard.press("Alt+BracketRight");
  await expect(section.locator(".unified-review-gap")).toHaveCount(0);
  await expect(section.locator(".unified-review-file-context")).toHaveAttribute(
    "aria-pressed",
    "true",
  );
  await page.keyboard.press("Alt+BracketRight");
  await expect(section.locator(".unified-review-gap")).toHaveCount(3);
});

test("clicking a line number opens the file at that line", async ({ page }) => {
  const section = await openSection(page);
  await section.locator(".line-numbers", { hasText: /^42$/ }).click();
  await expect(page.locator(".unified-review")).toHaveCount(0);
  await expectRevealed(page, sourceName, 42);
});
