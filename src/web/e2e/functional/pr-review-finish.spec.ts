import type { Page } from "@playwright/test";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { prCard as card, openCommentedFile } from "../harness/pull-request";

// Finishing a review: comments queue as pending drafts in place, then submit once with a verdict and summary
// through the stubbed store, which enforces GitHub's pending-review rules.
test.use({ prScenario: true });

async function commentOnLine(
  page: Page,
  line: number,
  body: string,
  button: string,
): Promise<void> {
  await page.locator(".monaco-editor .view-lines").first().click();
  await page.keyboard.press("ControlOrMeta+Home");
  for (let i = 1; i < line; i++) await page.keyboard.press("ArrowDown");
  await runCommand(page, "Comment on Line (Pull Request)");
  const draft = page.locator(".weavie-pr-card-draft");
  await draft.locator(".weavie-pr-composer-input").fill(body);
  await draft.getByRole("button", { name: button, exact: true }).click();
  await expect(card(page, body)).toBeVisible({ timeout: 10_000 });
}

// FLAKE 2026-10-14 06:13Z https://github.com/Kapps/weavie/actions/runs/38519737341 (macOS shard 4/6): the second
// "Comment on Line" never showed a draft. The first post's reply arrived after the next draft opened and its
// success handler closed that newer draft. Fixed in pr-comment-controller's submitDraft, which now closes only
// the draft it submitted.
// FLAKE 2026-10-15 ~05:00Z (macOS): the draft composer never appeared after "Comment on Line" and the test hit its 60s timeout.
// https://github.com/Kapps/weavie/actions/runs/38681930881/job/116250231850
// NOT FIXED: no root cause yet; the failing run's trace has no host-side error.
test("comments queue as drafts in place and submit once with a verdict", async ({ page }) => {
  await openCommentedFile(page);
  await commentOnLine(page, 5, "Posted right away.", "Comment now");
  await expect(card(page, "Posted right away.").locator(".weavie-pr-badge-pending")).toHaveCount(0);

  await commentOnLine(page, 6, "Queued for the review.", "Start review");
  const queued = card(page, "Queued for the review.");
  await expect(queued).toHaveClass(/weavie-pr-card-pending/);
  await expect(queued.locator(".weavie-pr-badge-pending")).toHaveText("Pending");

  // With a review pending, a reply joins it and "Comment now" is gone.
  const thread = card(page, "Why change this greeting?");
  await thread.locator(".weavie-pr-reply-stub").click();
  await expect(thread.getByRole("button", { name: "Comment now" })).toHaveCount(0);
  await expect(thread.locator(".weavie-pr-composer-note")).toHaveText("Review pending · 1 comment");
  await thread.locator(".weavie-pr-composer-input").fill("A queued reply.");
  await thread.locator(".weavie-pr-composer-input").press("ControlOrMeta+Enter");
  await expect(
    thread.locator(".weavie-pr-comment-draft", { hasText: "A queued reply." }),
  ).toBeVisible({
    timeout: 10_000,
  });

  // Discarding one draft leaves the other.
  const reply = thread.locator(".weavie-pr-comment-draft");
  await reply.hover();
  await reply.getByRole("button", { name: "Discard pending comment" }).click();
  await expect(thread.locator(".weavie-pr-comment-draft")).toHaveCount(0, { timeout: 10_000 });

  await runCommand(page, "Review Changes");
  const button = page.locator(".finish-review-button");
  await expect(button).toContainText("Finish review");
  await expect(button.locator(".finish-review-count")).toHaveText("1");
  await runCommand(page, "Finish Review…");
  const panel = page.locator(".finish-review-panel");
  await expect(panel.locator(".finish-review-draft")).toContainText("hello.ts:6");
  const summary = panel.locator("textarea");
  await expect(summary).toBeFocused();
  await summary.fill("One thing inline.");
  await panel.getByText("Request changes", { exact: true }).click();
  await summary.press("ControlOrMeta+Enter");

  await expect(panel).toHaveCount(0, { timeout: 10_000 });
  await expect(button).toHaveText("Review");
});

test("discarding the pending review asks first and deletes every draft", async ({ page }) => {
  await openCommentedFile(page);
  await commentOnLine(page, 5, "A draft to throw away.", "Start review");

  await runCommand(page, "Discard Pending Review");
  const dialog = page.getByRole("dialog").filter({ hasText: "Discard your pending review?" });
  await expect(dialog).toContainText("Your 1 pending comment on PR #101 will be deleted.");
  await dialog.getByRole("button", { name: "Discard" }).click();

  await expect(card(page, "A draft to throw away.")).toHaveCount(0, { timeout: 10_000 });
  await expect(page.locator(".finish-review-button")).toHaveText("Review");
});
