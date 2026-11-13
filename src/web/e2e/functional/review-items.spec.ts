import { execFileSync } from "node:child_process";
import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Locator, Page } from "@playwright/test";
import { awaitEditorReady, createSession, runCommand } from "../harness/actions";
import type { FakeStep } from "../harness/fake-claude";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";
import { prCard } from "../harness/pull-request";

// Next/Previous Comment or Hint: one sequence of a session's PR threads and review hints, in the review's file order,
// with its position in the review header and on the current item. See docs/specs/review-points-of-interest/.

const hint: FakeStep = {
  op: "hook",
  request: {
    hook_event_name: "PostToolUse",
    tool_name: "ReportFindings",
    cwd: "{{WORKSPACE}}",
    tool_input: {
      findings: [
        {
          file: "hello.ts",
          line: 5,
          summary: "The PR greeting is hardcoded",
          verdict: "CONFIRMED",
        },
      ],
    },
  },
};

test.use({ prScenario: true, fakeScript: { steps: [hint] } });

const position = (page: Page): Locator =>
  page.locator(".unified-review-header .review-item-position > span");
const popover = (page: Page): Locator => page.locator(".weavie-hint-hover");
const next = (page: Page) =>
  page.keyboard.press(
    process.platform === "darwin" ? "Meta+Alt+BracketRight" : "Control+Alt+BracketRight",
  );

// Opens PR #101's unified review: feature.ts (an open and a resolved thread) then hello.ts (a thread and a hint).
async function openReview(page: Page): Promise<void> {
  await runCommand(page, "Open Pull Request…");
  await expect(page.locator(".pr-suggestion-number", { hasText: "#101" })).toBeVisible();
  await page.locator(".session-prompt-input").press("Enter");
  await expect(page.locator(".unified-review .session-overview")).toBeVisible({ timeout: 20_000 });
  await expect(page.locator(".weavie-hint-pill")).toHaveCount(1, { timeout: 15_000 });
  await expect(position(page)).toHaveText("3");
}

test("steps by key across the review's files, wrapping, with the position in the header and on the item", async ({
  page,
}) => {
  test.slow();
  await openReview(page);
  const resolved = prCard(page, "Name it after the feature");
  await expect(resolved.locator(".weavie-pr-badge-resolved")).toHaveText("Resolved");
  await expect(resolved.locator(".weavie-pr-summary")).toBeVisible();

  // From the overview, the first step lands on the first item; it focuses the code, so the key steps on.
  await runCommand(page, "Next Comment or Hint");
  const flag = prCard(page, "Should this flag default to off?");
  await expect(flag).toHaveClass(/weavie-pr-card-current/);
  await expect(position(page)).toHaveText("1 of 3");
  await next(page);
  const greeting = prCard(page, "Why change this greeting?");
  await expect(greeting).toHaveClass(/weavie-pr-card-current/);
  await expect(position(page)).toHaveText("2 of 3");
  await expect(greeting.locator(".review-item-position > span")).toHaveText("2 of 3");
  await expect(flag).not.toHaveClass(/weavie-pr-card-current/);

  await next(page);
  await expect(popover(page).locator(".weavie-hint-title")).toHaveText(
    "The PR greeting is hardcoded",
  );
  await expect(popover(page).locator(".review-item-position > span")).toHaveText("3 of 3");
  await expect(greeting).not.toHaveClass(/weavie-pr-card-current/);

  await next(page);
  await expect(prCard(page, "Should this flag default to off?")).toHaveClass(
    /weavie-pr-card-current/,
  );
  await expect(popover(page)).toHaveCount(0);
  await expect(position(page)).toHaveText("1 of 3");

  await page.keyboard.press("Escape");
  await expect(position(page)).toHaveText("3");
  await page.mouse.wheel(0, 400);
  await expect(position(page)).toHaveText("3");
});

test("the filter hides a kind, adds resolved threads, and is remembered; Dismiss drops a hint", async ({
  page,
}) => {
  test.slow();
  await openReview(page);
  const filter = async (row: string) => {
    await page.getByRole("button", { name: "Filter Comments and Hints" }).click();
    await page.locator(".context-menu-item", { hasText: row }).click();
  };

  await filter("Hints");
  await expect(position(page)).toHaveText("2");
  await expect(page.locator(".weavie-hint-pill")).toHaveCount(0);
  await filter("Resolved and outdated");
  await expect(position(page)).toHaveText("3");
  await page.getByRole("button", { name: "Filter Comments and Hints" }).click();
  const rows = page.locator(".context-menu-item");
  await expect(rows.filter({ hasText: "Comments" }).locator(".context-menu-detail")).toHaveText(
    "2",
  );
  await expect(rows.filter({ hasText: "Hints" }).locator(".context-menu-check svg")).toHaveCount(0);
  await expect(rows.filter({ hasText: "Resolved" }).locator(".context-menu-check svg")).toHaveCount(
    1,
  );
  await page.keyboard.press("Escape");

  await page.reload({ waitUntil: "domcontentloaded" });
  await expect(page.locator("#splash")).toHaveCount(0, { timeout: 40_000 });
  await runCommand(page, "Review Changes");
  await expect(position(page)).toHaveText("3");
  await expect(page.locator(".weavie-hint-pill")).toHaveCount(0);

  await filter("Hints");
  await filter("Resolved and outdated");
  await expect(position(page)).toHaveText("3");
  await runCommand(page, "Previous Comment or Hint");
  await expect(popover(page).locator(".weavie-hint-title")).toHaveText(
    "The PR greeting is hardcoded",
  );
  await popover(page).getByRole("button", { name: "Dismiss hint" }).click();
  await expect(position(page)).toHaveText("2");
});

test.describe("a hint on a line the unified review folds away", () => {
  test.use({ prScenario: false, fakeScript: null });

  test("the first step opens the fold and shows the hint's popover", async ({ page, weavie }) => {
    await awaitEditorReady(page);
    await createSession(page, { branch: "folded-hint", provider: "fake-acp" });
    const [worktree] = sessionWorktrees(weavie.workspace);
    const lines = Array.from({ length: 160 }, (_, i) => `// line ${i + 1}`);
    lines[4] = "// changed five";
    await writeFile(join(worktree!, "long.ts"), `${lines.join("\n")}\n`);
    execFileSync("git", ["commit", "-qam", "change"], { cwd: worktree });
    const composer = page.locator(
      '[data-surface="structured-agent"] [data-agent-composer] textarea',
    );
    await composer.click();
    await composer.fill(
      `mcp:addReviewHint:${JSON.stringify({ path: "long.ts", line: 80, severity: "bug", text: "Folded hint" })}`,
    );
    await composer.press("Enter");
    await expect(page.locator('[data-surface="structured-agent"]')).toContainText(
      "Hint added at long.ts:80",
    );
    await runCommand(page, "Diff Against…");
    await page.locator(".session-prompt-input").fill("HEAD^");
    await page.locator(".session-prompt-input").press("Enter");
    // Diff Against opens on the first changed file; the folding happens in the unified review.
    await expect(page.locator(".weavie-inline-toolbar, .unified-review").first()).toBeVisible({
      timeout: 30_000,
    });
    if ((await page.locator(".unified-review").count()) === 0)
      await runCommand(page, "Review Changes");
    await expect(page.locator(".unified-review")).toBeVisible({ timeout: 30_000 });

    await runCommand(page, "Next Comment or Hint");
    await expect(page.locator(".view-line", { hasText: "// line 80" })).toBeVisible();
    await expect(popover(page).locator(".weavie-hint-title")).toBeVisible();
    await expect(popover(page).locator(".weavie-hint-title")).toHaveText("Folded hint");
  });
});
