import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// The Open-PR journey end to end against a stubbed PR provider + a local "origin" with a base/head diff
// (prScenario): pick the PR, check out its branch as a session, and walk its base→head diff in the inline-diff
// navigator. See docs/specs/open-pr.md (Phase 2).
test.use({ prScenario: true });

test("opening a PR checks out its branch and pops up the diff navigator", async ({ page }) => {
  // The repo starts with one session (the primary checkout).
  await expect(page.locator(".session-chip")).toHaveCount(1);

  // Open the picker; it lists the stubbed PR #101.
  await runCommand(page, "Open Pull Request");
  await expect(page.locator(".session-prompt")).toBeVisible();
  await expect(page.locator(".pr-suggestion-number", { hasText: "#101" })).toBeVisible();

  // Pick it (Enter on the highlighted row) → a second session lands on the rail, on the PR's head branch.
  await page.locator(".session-prompt-input").press("Enter");
  await expect(page.locator(".session-chip")).toHaveCount(2, { timeout: 20_000 });

  // The diff navigator surfaces automatically with the PR's changes: a floating toolbar over the editor and
  // bright added lines (feature.ts is a new file — all additions).
  const toolbar = page.locator(".weavie-inline-toolbar");
  await expect(toolbar).toBeVisible({ timeout: 20_000 });
  await expect(page.locator(".weavie-inline-added").first()).toBeVisible();

  // It's a two-file walk (feature.ts added, hello.ts modified): the stacked label names the current file and
  // ← / → moves between them.
  const label = page.locator(".weavie-inline-stack-name");
  await expect(label).toHaveText(/feature\.ts|hello\.ts/);
  const first = (await label.textContent())?.trim() ?? "";
  await page.keyboard.press("ControlOrMeta+ArrowRight");
  await expect
    .poll(async () => (await label.textContent())?.trim(), { timeout: 10_000 })
    .not.toBe(first);
  await expect(page.locator(".weavie-inline-added").first()).toBeVisible();
});
