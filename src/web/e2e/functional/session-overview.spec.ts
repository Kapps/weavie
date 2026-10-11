import type { Page } from "@playwright/test";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// The session overview at the top of the review tab (docs/specs/session-overview.md): a PR session adds its
// description, conversation, checks, and reviewers; every session gets its title and changes, and the review's
// commits when it compares against a branch. Runs against the stubbed PR provider (prScenario).
test.use({ prScenario: true });

const overview = (page: Page) => page.locator(".session-overview");
const section = (page: Page, id: string) =>
  page.locator(`.session-overview-section[data-section="${id}"]`);

async function openPrReview(page: Page): Promise<void> {
  await runCommand(page, "Open Pull Request…");
  await expect(page.locator(".pr-suggestion-number", { hasText: "#101" })).toBeVisible();
  await page.locator(".session-prompt-input").press("Enter");
  await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 20_000 });
  await runCommand(page, "Show Session Overview");
  await expect(overview(page)).toBeVisible();
}

test("a PR session's overview shows its discussion, checks, and commits, and posts a PR comment", async ({
  page,
}) => {
  await openPrReview(page);

  await expect(overview(page).locator(".session-overview-pr-title")).toContainText(
    "Add a feature and tweak the greeting",
  );
  await expect(overview(page).locator(".session-overview-pill-open")).toBeVisible();
  await expect(section(page, "description")).toContainText(
    "Tweaks the greeting and adds a feature flag.",
  );
  await expect(section(page, "conversation")).toContainText(
    "Can we keep the old greeting behind the flag?",
  );
  await expect(section(page, "conversation")).toContainText("requested changes");
  await expect(section(page, "checks").locator(".session-overview-meta")).toHaveText(
    "1 of 2 passed",
  );
  // Failing checks lead.
  await expect(section(page, "checks").locator(".session-overview-row").first()).toHaveClass(
    /session-overview-check-failure/,
  );
  await expect(section(page, "reviewers")).toContainText("Changes requested");
  await expect(section(page, "reviewers")).toContainText("erin");
  await expect(section(page, "commits")).toContainText("pr changes");
  await expect(section(page, "changes")).toContainText("PR #101");
  await expect(section(page, "changes").locator(".unified-review-tree")).toContainText("hello.ts");
  // The review-submit control sits in the overview, not twice.
  await expect(page.locator(".finish-review-button")).toHaveCount(1);
  await expect(overview(page).locator(".finish-review-button")).toBeVisible();

  // A PR-level comment posts immediately and lands in the conversation.
  await section(page, "conversation")
    .getByRole("button", { name: /Comment on this pull request/ })
    .click();
  await section(page, "conversation")
    .locator(".weavie-pr-composer-input")
    .fill("Thanks, looking now.");
  await section(page, "conversation").getByRole("button", { name: "Comment", exact: true }).click();
  await expect(
    section(page, "conversation").locator(".session-overview-comment").last(),
  ).toContainText("Thanks, looking now.", { timeout: 10_000 });
  await expect(section(page, "conversation").locator(".weavie-pr-composer")).toHaveCount(0);

  // Scrolled into the diffs, the header names the PR and hosts the submit control; Overview returns to the top.
  await section(page, "changes")
    .locator(".unified-review-tree-row", { hasText: "hello.ts" })
    .click();
  const header = page.locator(".unified-review-header");
  await expect(header.locator(".unified-review-identity")).toContainText(
    "Add a feature and tweak the greeting",
  );
  await expect(header.locator(".unified-review-failing")).toHaveText("1 check failing");
  await expect(header.locator(".finish-review-button")).toBeVisible();
  await expect(page.locator(".finish-review-button")).toHaveCount(1);
  await header.getByRole("button", { name: "Overview" }).click();
  await expect(header.locator(".unified-review-identity")).toHaveCount(0);
  await expect(overview(page).locator(".finish-review-button")).toBeVisible();
});

test("collapsed sections are remembered across sessions and reloads", async ({ page }) => {
  await openPrReview(page);
  const commits = section(page, "commits");
  await commits.locator(".session-overview-heading").click();
  await expect(commits.locator(".session-overview-heading")).toHaveAttribute(
    "aria-expanded",
    "false",
  );
  await expect(commits.locator(".session-overview-commits")).toHaveCount(0);

  await page.reload({ waitUntil: "domcontentloaded" });
  await expect(page.locator("#splash")).toHaveCount(0, { timeout: 40_000 });
  await runCommand(page, "Show Session Overview");
  await expect(section(page, "commits").locator(".session-overview-heading")).toHaveAttribute(
    "aria-expanded",
    "false",
  );
});

test("a session without a PR or changes still opens on its overview", async ({ page }) => {
  await runCommand(page, "Show Session Overview");
  await expect(overview(page).locator(".session-overview-branch")).toHaveText("main");
  await expect(overview(page).locator(".session-overview-pill-none")).toHaveText("No pull request");
  await expect(section(page, "changes")).toContainText("Nothing to review yet.");
  await expect(section(page, "conversation")).toHaveCount(0);
  await expect(section(page, "commits")).toHaveCount(0);
});
