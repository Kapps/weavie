import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { walkToChangedFile } from "../harness/navigator";

// SCENE 2 (video tour): a PR review's applied toolbar now carries Keep (accept) + Revert (reject) BESIDE
// Comment — the review engine is unified, not read-only. Modeled on functional/open-pr.spec.ts, with holds so
// the recording SHOWS each state. Records a .webm; the committed regression spec already lives in functional/.
test.use({ prScenario: true });

const hold = (page: import("@playwright/test").Page, ms: number) => page.waitForTimeout(ms);

test("Open PR: Keep + Revert + Comment coexist; reply to a review thread", async ({ page }) => {
  await runCommand(page, "Open Pull Request");
  await expect(page.locator(".pr-suggestion-number", { hasText: "#101" })).toBeVisible();
  await hold(page, 1200);
  await page.locator(".session-prompt-input").press("Enter");

  const toolbar = page.locator(".weavie-inline-toolbar");
  await expect(toolbar).toBeVisible({ timeout: 20_000 });
  await expect(page.locator(".weavie-inline-added").first()).toBeVisible();

  // All three actions on one applied toolbar — Keep + Revert (the new part) alongside Comment.
  await expect(toolbar.locator(".weavie-inline-accept")).toBeVisible();
  await expect(toolbar.locator(".weavie-inline-reject")).toBeVisible();
  await expect(toolbar.locator(".weavie-inline-comment")).toBeVisible();
  await toolbar.locator(".weavie-inline-accept").hover();
  await hold(page, 2000);
  await toolbar.locator(".weavie-inline-comment").hover();
  await hold(page, 1500);

  // Walk to hello.ts (the commented file; the navigator auto-opens feature.ts first).
  await walkToChangedFile(page, "hello.ts");
  await expect(
    page.locator(".weavie-pr-comment-body", { hasText: "Why change this greeting?" }),
  ).toBeVisible({ timeout: 10_000 });
  await hold(page, 1800);

  // Reply in the thread → the reply round-trips through the stubbed comment store and appears.
  const thread = page.locator(".weavie-pr-thread").first();
  await thread.locator(".weavie-pr-composer-input").fill("Addressed in the latest push.");
  await hold(page, 800);
  await thread.locator(".weavie-pr-composer-submit").click();
  await expect(
    page.locator(".weavie-pr-comment-body", { hasText: "Addressed in the latest push." }),
  ).toBeVisible({ timeout: 10_000 });
  await hold(page, 2200);
});
