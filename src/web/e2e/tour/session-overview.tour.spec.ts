import type { Page } from "@playwright/test";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Video tour of the session overview (docs/specs/session-overview.md): a PR session's overview, a PR-level
// comment, the diffs following in the same scroll under a sticky PR header, and a session without a PR.
// Full-resolution video: the default scales the clip down to 800px wide.
test.use({ prScenario: true, video: { mode: "on", size: { width: 1280, height: 800 } } });
test.setTimeout(150_000);

const hold = (page: Page, ms: number) => page.waitForTimeout(ms);
const overview = (page: Page) => page.locator(".session-overview");
const section = (page: Page, id: string) =>
  page.locator(`.session-overview-section[data-section="${id}"]`);
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

for (const theme of ["dark", "light"] as const) {
  test(`Session overview tour (${theme})`, async ({ page }) => {
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    page.on("pageerror", (e) => errors.push(`[pageerror] ${String(e)}`));
    await useTheme(page, theme);

    // (1) Open PR #101, then Show Session Overview.
    await runCommand(page, "Open Pull Request…");
    await expect(page.locator(".pr-suggestion-number", { hasText: "#101" })).toBeVisible();
    await hold(page, 1200);
    await page.locator(".session-prompt-input").press("Enter");
    await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 20_000 });
    // The harness has no TypeScript language server; its unavailable toast would cover the overview.
    await expect(page.locator(".toast", { hasText: "language intelligence" })).toBeVisible();
    await dismissToasts(page);
    await hold(page, 800);
    await runCommand(page, "Show Session Overview");
    await expect(overview(page)).toBeVisible();
    await hold(page, 1500);
    await dismissToasts(page);
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
    for (const id of ["checks", "reviewers", "commits", "changes"]) {
      await section(page, id).scrollIntoViewIfNeeded();
      await hold(page, 1300);
    }
    await section(page, "conversation").scrollIntoViewIfNeeded();
    await hold(page, 800);

    // (2) A PR-level comment from the Conversation section.
    const conversation = section(page, "conversation");
    await conversation.getByRole("button", { name: /Comment on this pull request/ }).click();
    const input = conversation.locator(".weavie-pr-composer-input");
    await input.pressSequentially("Thanks, looking now.", { delay: 50 });
    await hold(page, 900);
    await conversation.getByRole("button", { name: "Comment", exact: true }).click();
    await expect(conversation.locator(".session-overview-comment").last()).toContainText(
      "Thanks, looking now.",
      { timeout: 10_000 },
    );
    await expect(conversation.locator(".weavie-pr-composer")).toHaveCount(0);
    await hold(page, 2000);
    await conversation.screenshot({
      path: `${shots}/session-overview-${theme}-comment.png`,
    });

    // (3) Collapse Commits, then jump to hello.ts: the diffs follow in the same scroll under a sticky header.
    const commits = section(page, "commits");
    await commits.scrollIntoViewIfNeeded();
    await hold(page, 800);
    await commits.locator(".session-overview-heading").click();
    await expect(commits.locator(".session-overview-heading")).toHaveAttribute(
      "aria-expanded",
      "false",
    );
    await expect(commits.locator(".session-overview-commits")).toHaveCount(0);
    await hold(page, 1300);
    const row = section(page, "changes").locator(".unified-review-tree-row", {
      hasText: "hello.ts",
    });
    await row.scrollIntoViewIfNeeded();
    await row.hover();
    await hold(page, 1000);
    await row.click();
    const header = page.locator(".unified-review-header");
    await expect(header.locator(".unified-review-identity")).toContainText(
      "Add a feature and tweak the greeting",
    );
    await expect(header.locator(".unified-review-failing")).toHaveText("1 check failing");
    await expect(header.locator(".finish-review-button")).toBeVisible();
    await expect(page.locator(".finish-review-button")).toHaveCount(1);
    const box = await header.locator(".finish-review-button").boundingBox();
    console.log("header finish-review box:", JSON.stringify(box));
    await hold(page, 2500);
    await page.screenshot({ path: `${shots}/session-overview-${theme}-diffs.png` });
    const back = header.getByRole("button", { name: "Overview" });
    await back.hover();
    await hold(page, 1000);
    await back.click();
    await expect(header.locator(".unified-review-identity")).toHaveCount(0);
    await expect(overview(page).locator(".finish-review-button")).toBeVisible();
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

    console.log("console errors:", JSON.stringify(errors));
    expect(errors).toEqual([]);
  });
}
