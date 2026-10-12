import { execFileSync } from "node:child_process";
import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Locator, Page } from "@playwright/test";
import { createAcpSession } from "../harness/acp-session";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

// Video tour of the session overview (docs/specs/session-overview.md): opening a PR lands on its overview, a
// PR-level comment, the diffs following in the same scroll under a sticky PR header, a session without a PR, and
// the status bar's diff totals opening the branch's review. Full-resolution video: the default scales to 800px.
test.use({ prScenario: true, video: { mode: "on", size: { width: 1280, height: 800 } } });
test.setTimeout(180_000);

const hold = (page: Page, ms: number) => page.waitForTimeout(ms);
const overview = (page: Page) => page.locator(".session-overview");
const section = (page: Page, id: string) =>
  page.locator(`.session-overview-section[data-section="${id}"]`);
const heading = (page: Page, id: string) => section(page, id).locator(".session-overview-heading");
const shots = "e2e/.recordings";
async function dismissToasts(page: Page): Promise<void> {
  const close = page.locator(".toast:not(.leaving) .toast-close");
  while ((await close.count()) > 0) await close.first().click();
  await expect(page.locator(".toast")).toHaveCount(0);
}
async function useTheme(page: Page, appearance: "dark" | "light"): Promise<void> {
  await runCommand(page, "Select Color Theme…");
  const picker = page.getByRole("dialog", { name: "Select Color Theme" });
  await picker.getByLabel("Theme appearance").selectOption(appearance);
  const filter = picker.getByRole("combobox", { name: "Filter themes", exact: true });
  await filter.fill(appearance === "dark" ? "Weavie Dark" : "Weavie Light");
  await filter.press("Enter");
  await expect(page.locator("html")).toHaveAttribute("data-theme-type", appearance);
}
// Headless Chromium never paints native title tooltips, so the tour draws the hovered element's title beneath it.
async function hoverWithTitle(page: Page, target: Locator, ms: number): Promise<string> {
  await target.hover();
  const title = (await target.getAttribute("title")) ?? "";
  await target.evaluate((el, text) => {
    const r = el.getBoundingClientRect();
    const tip = document.createElement("div");
    tip.id = "tour-tooltip";
    tip.textContent = text;
    Object.assign(tip.style, {
      position: "fixed",
      left: `${r.left}px`,
      top: `${r.bottom + 6}px`,
      zIndex: "99999",
      padding: "3px 7px",
      font: "12px sans-serif",
      background: "#fffbe6",
      color: "#222",
      border: "1px solid #888",
      boxShadow: "0 2px 6px rgba(0,0,0,.3)",
      pointerEvents: "none",
    });
    document.body.append(tip);
  }, title);
  await hold(page, ms);
  await page.evaluate(() => document.getElementById("tour-tooltip")?.remove());
  return title;
}

for (const theme of ["dark", "light"] as const) {
  test(`Session overview tour (${theme})`, async ({ page, weavie }) => {
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    page.on("pageerror", (e) => errors.push(`[pageerror] ${String(e)}`));
    await useTheme(page, theme);

    // (1) Open PR #101: the review lands on the session overview, not the first file.
    await runCommand(page, "Open Pull Request…");
    await expect(page.locator(".pr-suggestion-number", { hasText: "#101" })).toBeVisible();
    await hold(page, 1200);
    await page.locator(".session-prompt-input").press("Enter");
    await expect(page.locator(".unified-review .session-overview")).toBeVisible({
      timeout: 20_000,
    });
    await dismissToasts(page);
    const title = overview(page).locator(".session-overview-pr-title");
    await expect(title).toContainText("Add a feature and tweak the greeting");
    const byline = overview(page).locator(".session-overview-byline");
    await expect(byline).toContainText("wants to merge");
    await expect(byline.locator(".finish-review-button")).toBeVisible();
    await expect(overview(page).locator(".session-overview-pill-open")).toBeVisible();
    await expect(section(page, "description")).toContainText(
      "Tweaks the greeting and adds a feature flag.",
    );
    await expect(section(page, "conversation")).toContainText(
      "Can we keep the old greeting behind the flag?",
    );
    await expect(section(page, "checks").locator(".session-overview-row").first()).toHaveClass(
      /session-overview-check-failure/,
    );
    await expect(section(page, "reviewers")).toContainText("Changes requested");
    await expect(section(page, "commits")).toContainText("pr changes");
    await expect(section(page, "changes").locator(".unified-review-tree")).toContainText(
      "hello.ts",
    );
    await hold(page, 2500);
    await page.screenshot({ path: `${shots}/session-overview-${theme}-top.png` });

    // The #N is the PR's link; "opened … ago" shows the full local date on hover.
    const link = await hoverWithTitle(page, title.locator(".session-overview-number"), 1800);
    expect(link).toContain("https://github.com/acme/demo/pull/101");
    const opened = await hoverWithTitle(page, byline.locator("time"), 2500);
    console.log("opened tooltip:", opened);
    expect(opened).toMatch(/\d{4}/);
    await byline.locator(".finish-review-button").hover();
    await hold(page, 1200);

    // Walk the sections; the review scrolls a focused heading into view.
    for (const id of ["checks", "reviewers", "commits", "changes"]) {
      await heading(page, id).focus();
      await hold(page, 1300);
    }

    // (2) A PR-level comment from the Conversation section.
    const conversation = section(page, "conversation");
    await conversation.getByRole("button", { name: /Comment on this pull request/ }).focus();
    await hold(page, 800);
    await page.keyboard.press("Enter");
    const input = conversation.locator(".weavie-pr-composer-input");
    await expect(input).toBeFocused();
    await input.pressSequentially("Thanks, looking now.", { delay: 50 });
    await hold(page, 900);
    await input.press("ControlOrMeta+Enter");
    await expect(conversation.locator(".session-overview-comment").last()).toContainText(
      "Thanks, looking now.",
      { timeout: 10_000 },
    );
    await expect(conversation.locator(".weavie-pr-composer")).toHaveCount(0);
    await hold(page, 2000);
    await conversation.screenshot({ path: `${shots}/session-overview-${theme}-comment.png` });

    // (3) Collapse Commits, then jump to hello.ts: the diffs follow in the same scroll under a sticky header.
    await heading(page, "commits").focus();
    await hold(page, 800);
    await page.keyboard.press("Enter");
    await expect(heading(page, "commits")).toHaveAttribute("aria-expanded", "false");
    await expect(section(page, "commits").locator(".session-overview-commits")).toHaveCount(0);
    await hold(page, 1300);
    await section(page, "changes")
      .locator(".unified-review-tree-row", { hasText: "hello.ts" })
      .focus();
    await hold(page, 1000);
    await page.keyboard.press("Enter");
    const header = page.locator(".unified-review-header");
    await expect(header.locator(".unified-review-identity")).toContainText(
      "Add a feature and tweak the greeting",
    );
    await expect(header.locator(".unified-review-failing")).toHaveText("1 check failing");
    await expect(header.locator(".finish-review-button")).toBeVisible();
    await expect(page.locator(".finish-review-button")).toHaveCount(1);
    await hold(page, 2500);
    await page.screenshot({ path: `${shots}/session-overview-${theme}-diffs.png` });
    const back = header.getByRole("button", { name: "Overview" });
    await back.hover();
    await hold(page, 1000);
    await back.click();
    await expect(header.locator(".unified-review-identity")).toHaveCount(0);
    await expect(byline.locator(".finish-review-button")).toBeVisible();
    await hold(page, 2000);

    // (4) The original session has no PR: its overview names the branch.
    await page.locator(".session-chip").first().click();
    await expect(page.locator(".weavie-inline-toolbar")).toHaveCount(0, { timeout: 10_000 });
    await hold(page, 1000);
    await runCommand(page, "Show Session Overview");
    await expect(overview(page).locator(".session-overview-branch")).toHaveText("main");
    await expect(overview(page).locator(".session-overview-pill-none")).toHaveText(
      "No pull request",
    );
    await hold(page, 2500);
    await page.screenshot({ path: `${shots}/session-overview-${theme}-no-pr.png` });

    // (5) An agent session with a commit and an edit: the status bar's totals offer its diffs; Branch opens the
    // whole branch against origin/main on its overview.
    await createAcpSession(page, "status-diff");
    const worktree = sessionWorktrees(weavie.workspace).find((p) => p.endsWith("status-diff"));
    if (!worktree) throw new Error("no status-diff worktree");
    const git = (...args: string[]) =>
      execFileSync("git", ["-c", "commit.gpgsign=false", ...args], { cwd: worktree });
    await writeFile(join(worktree, "notes.md"), "# Notes\n\nShip the overview.\n");
    git("add", "notes.md");
    git("commit", "-q", "-m", "Add release notes");
    await writeFile(join(worktree, "README.md"), "# Changed\n");
    const totals = page.locator(".agent-status-diff");
    await expect(totals).toBeVisible({ timeout: 20_000 });
    await hold(page, 1200);
    await totals.hover();
    await hold(page, 1000);
    await totals.click();
    const menu = page.locator(".context-menu");
    for (const label of ["Current review", "Uncommitted", "Last commit", "Branch"]) {
      await expect(menu).toContainText(label);
    }
    for (const label of ["Uncommitted", "Last commit", "Branch"]) {
      await menu.getByText(label, { exact: true }).hover();
      await hold(page, 700);
    }
    await page.screenshot({ path: `${shots}/session-overview-${theme}-totals-menu.png` });
    await menu.getByText("Branch", { exact: true }).click();
    await expect(section(page, "changes")).toContainText("vs origin/main", { timeout: 20_000 });
    // Commits stays collapsed from the PR session (collapse is remembered globally); expand it.
    await expect(heading(page, "commits")).toHaveAttribute("aria-expanded", "false");
    await hold(page, 1200);
    await heading(page, "commits").focus();
    await page.keyboard.press("Enter");
    await expect(section(page, "commits")).toContainText("Add release notes");
    const tree = section(page, "changes").locator(".unified-review-tree");
    await expect(tree).toContainText("notes.md");
    await expect(tree).toContainText("README.md");
    await hold(page, 3000);
    await page.screenshot({ path: `${shots}/session-overview-${theme}-branch.png` });

    console.log("console errors:", JSON.stringify(errors));
    expect(errors).toEqual([]);
  });
}
