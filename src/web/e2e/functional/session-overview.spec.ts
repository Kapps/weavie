import { execFileSync } from "node:child_process";
import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { createAcpSession } from "../harness/acp-session";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

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
  // The description renders like GitHub: comments hidden, allowed HTML live, task lists as checkboxes.
  const description = section(page, "description");
  await expect(description).not.toContainText("template-comment");
  await expect(description.locator("input[type=checkbox]")).toHaveCount(2);
  await expect(description.locator("input[type=checkbox]").last()).toBeChecked();
  const hostile = description.locator(".agent-markdown > div");
  await expect(hostile).toHaveText("hostile");
  await expect(hostile).not.toHaveAttribute("style");
  await expect(hostile).not.toHaveAttribute("data-agent-kind");
  await expect(description.locator("style")).toHaveCount(0);
  await expect(description.locator("details summary b")).toHaveText("More options");
  await expect(description.locator("details i")).toBeHidden();
  await description.locator("details summary").click();
  await expect(description.locator("details i")).toHaveText("Optional");
  await expect(description).not.toContainText("[x]");
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
  // A long unbroken line in the description wraps inside its column instead of pushing the sidebar away.
  await expect(section(page, "checks")).toBeInViewport({ ratio: 1 });
  await expect(section(page, "commits")).toContainText("pr changes");
  await expect(section(page, "changes")).toContainText("PR #101");
  await expect(section(page, "changes").locator(".unified-review-tree")).toContainText("hello.ts");
  // The review-submit control sits in the overview, not twice.
  await expect(page.locator(".finish-review-button")).toHaveCount(1);
  await expect(overview(page).locator(".finish-review-button")).toBeVisible();

  // A PR-level comment posts immediately and lands in the conversation. Keyboard-driven: the review scrolls the
  // focused control into view, which a pointer click below the fold can't reach.
  await section(page, "conversation")
    .getByRole("button", { name: /Comment on this pull request/ })
    .focus();
  await page.keyboard.press("Enter");
  const input = section(page, "conversation").locator(".weavie-pr-composer-input");
  await expect(input).toBeFocused();
  await input.fill("Thanks, looking now.");
  await input.press("ControlOrMeta+Enter");
  await expect(
    section(page, "conversation").locator(".session-overview-comment").last(),
  ).toContainText("Thanks, looking now.", { timeout: 10_000 });
  await expect(section(page, "conversation").locator(".weavie-pr-composer")).toHaveCount(0);

  // Scrolled into the diffs, the header names the PR and hosts the submit control; Overview returns to the top.
  const helloRow = section(page, "changes").locator(".unified-review-tree-row", {
    hasText: "hello.ts",
  });
  await helloRow.focus();
  await page.keyboard.press("Enter");
  const header = page.locator(".unified-review-header");
  await expect(header.locator(".unified-review-identity")).toContainText(
    "Add a feature and tweak the greeting",
  );
  await expect(header.locator(".unified-review-failing")).toHaveText("1 check failing");
  await expect(header.locator(".unified-review-identity-title")).toBeInViewport({ ratio: 1 });
  // Truncated if it must be, never squeezed away.
  expect(
    (await header.locator(".unified-review-identity-title").boundingBox())!.width,
  ).toBeGreaterThan(120);
  await expect(header.locator(".unified-review-failing")).toBeInViewport({ ratio: 1 });
  // The heading gives way so the header's actions stay on screen at the default window width.
  await expect(header.locator(".finish-review-button")).toBeInViewport({ ratio: 1 });
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

test("clicking the status bar's diff totals offers the session's diffs, each opening on its overview", async ({
  page,
  weavie,
}) => {
  // The status line belongs to the native agent pane.
  await createAcpSession(page, "status-diff");
  const worktree = sessionWorktrees(weavie.workspace).find((path) => path.endsWith("status-diff"));
  await writeFile(join(worktree!, "README.md"), "# Changed\n");
  const totals = page.locator(".agent-status-diff");
  await expect(totals).toBeVisible({ timeout: 20_000 });
  await totals.click();
  const menu = page.locator(".context-menu");
  await expect(menu).toContainText("Current review");
  await expect(menu).toContainText("Last commit");
  await expect(menu).toContainText("Branch");
  await menu.getByText("Uncommitted", { exact: true }).click();
  await expect(section(page, "changes")).toContainText("vs HEAD");
  await expect(section(page, "changes").locator(".unified-review-tree")).toContainText("README.md");
});

test("the terminal footer's branch opens the branch's diff on the overview with its commits", async ({
  page,
  weavie,
}) => {
  const git = (...args: string[]) =>
    execFileSync("git", ["-c", "commit.gpgsign=false", ...args], { cwd: weavie.workspace });
  git("checkout", "-q", "-b", "feature/overview");
  await writeFile(join(weavie.workspace, "branch.txt"), "on the branch\n");
  git("add", "branch.txt");
  git("commit", "-q", "-m", "Add the branch file");
  const branch = page.locator('.terminal-surface[data-kind="terminal:shell"] .footer-branch');
  await expect(branch).toContainText("feature/overview", { timeout: 20_000 });
  await branch.click();
  await expect(section(page, "commits")).toContainText("Add the branch file", { timeout: 20_000 });
  await expect(section(page, "changes")).toContainText("vs origin/main");
  await expect(section(page, "changes").locator(".unified-review-tree")).toContainText(
    "branch.txt",
  );
  const commit = section(page, "commits").locator("li", { hasText: "Add the branch file" });
  await expect(commit).not.toHaveClass(/session-overview-outside/);

  // A narrower review still lists the branch's commits, dimming the ones it doesn't span.
  await writeFile(join(weavie.workspace, "branch.txt"), "on the branch\nand uncommitted\n");
  await runCommand(page, "Diff Against HEAD");
  await page.getByRole("button", { name: "Switch", exact: true }).click();
  await expect(section(page, "changes")).toContainText("vs HEAD");
  await expect(commit).toHaveClass(/session-overview-outside/);
  await expect(section(page, "commits").locator(".session-overview-meta")).toHaveText(
    "0 of 1 in this review",
  );
});
