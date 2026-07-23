import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { awaitReviewSet, collectChangedFiles } from "../harness/navigator";

// SCENARIO 2: two PR sessions open at once (#101: feature.ts + hello.ts; #102: widget.ts + notes.txt). Each
// session must show ITS OWN changed files / merge-base diff — never the other PR's. ReviewIdentity is owned by
// worktree path and ActiveReview() resolves by the active session, so this is the core of that design.
test.use({ prScenario: true });

const toolbar = ".weavie-inline-toolbar";
const chips = ".session-chip";

async function openPrByNumber(
  page: import("@playwright/test").Page,
  n: number,
  expectedChips: number,
): Promise<void> {
  await runCommand(page, "Open Pull Request");
  await expect(page.locator(".session-prompt")).toBeVisible();
  await page.locator(".session-prompt-input").fill(`#${n}`);
  await expect(page.locator(".pr-suggestion-number", { hasText: `#${n}` })).toBeVisible();
  await page.locator(".session-prompt-input").press("Enter");
  await expect(page.locator(chips)).toHaveCount(expectedChips, { timeout: 25_000 });
  await expect(page.locator(toolbar)).toBeVisible({ timeout: 20_000 });
}

test("S2: two PR sessions each show only their own changed files", async ({ page }) => {
  // Heavyweight: provisions two real PR worktrees (each a fake-claude + merge-base git diff) and walks their
  // navigators. On the slow, serialized hosted Windows/macOS runners the legitimate work outlasts the 30s
  // default even with no contention, so give it the room — this marks the test slow, it does not retry it.
  test.slow();
  // Open PR #101 (second chip), then PR #102 (third chip). Both are now live sessions on the rail.
  await openPrByNumber(page, 101, 2);
  await openPrByNumber(page, 102, 3);

  // The active session is #102 — its navigator must show widget.ts / notes.txt and NOT #101's files.
  await awaitReviewSet(page, ["widget.ts", "notes.txt"]);
  const filesOn102 = await collectChangedFiles(page);
  expect([...filesOn102].sort()).toEqual(["notes.txt", "widget.ts"]);

  // Switch back to the #101 session (second chip) — its navigator must show feature.ts / hello.ts only.
  // Walk only once the incoming PR's diff is bound: until then the outgoing session's navigator is still
  // (legitimately) on screen, and walking it would read the previous PR's files.
  await page.locator(chips).nth(1).click();
  await expect(page.locator(toolbar)).toBeVisible({ timeout: 15_000 });
  await awaitReviewSet(page, ["feature.ts", "hello.ts"]);
  const filesOn101 = await collectChangedFiles(page);
  expect([...filesOn101].sort()).toEqual(["feature.ts", "hello.ts"]);

  // Switch to #102 again — still only its own files (no leak accumulated across switches).
  await page.locator(chips).nth(2).click();
  await expect(page.locator(toolbar)).toBeVisible({ timeout: 15_000 });
  await awaitReviewSet(page, ["widget.ts", "notes.txt"]);
  const filesOn102Again = await collectChangedFiles(page);
  expect([...filesOn102Again].sort()).toEqual(["notes.txt", "widget.ts"]);
});
