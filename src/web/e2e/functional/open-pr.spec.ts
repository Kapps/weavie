import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { navChord, walkToChangedFile } from "../harness/navigator";

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
  await page.keyboard.press(navChord("ArrowRight"));
  await expect
    .poll(async () => (await label.textContent())?.trim(), { timeout: 10_000 })
    .not.toBe(first);
  await expect(page.locator(".weavie-inline-added").first()).toBeVisible();
});

test("typing #N opens a PR directly by number", async ({ page }) => {
  await runCommand(page, "Open Pull Request");
  await expect(page.locator(".session-prompt")).toBeVisible();

  // Type the number directly — no dependence on the list (the host resolves its branch by number).
  await page.locator(".session-prompt-input").fill("#101");
  await expect(page.locator(".pr-suggestion-number", { hasText: "#101" })).toBeVisible();
  // The row previews the resolved PR's real title (debounced resolve-by-number).
  await expect(page.locator(".pr-suggestion-title", { hasText: "Add a feature" })).toBeVisible({
    timeout: 10_000,
  });
  await page.locator(".session-prompt-input").press("Enter");

  await expect(page.locator(".session-chip")).toHaveCount(2, { timeout: 20_000 });
  await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 20_000 });
});

test("a PR's review comments render, reply, and add", async ({ page }) => {
  await runCommand(page, "Open Pull Request");
  await expect(page.locator(".pr-suggestion-number", { hasText: "#101" })).toBeVisible();
  await page.locator(".session-prompt-input").press("Enter");
  await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 20_000 });

  // Walk to hello.ts (the commented file; the navigator auto-opens feature.ts first).
  await walkToChangedFile(page, "hello.ts");

  // The seeded review comment shows in a thread on the diff.
  await expect(
    page.locator(".weavie-pr-comment-body", { hasText: "Why change this greeting?" }),
  ).toBeVisible({
    timeout: 10_000,
  });

  // Reply in the thread → the reply appears (round-tripped through the stubbed comment store).
  const thread = page.locator(".weavie-pr-thread").first();
  await thread.locator(".weavie-pr-composer-input").fill("Addressed in the latest push.");
  await thread.locator(".weavie-pr-composer-submit").click();
  await expect(
    page.locator(".weavie-pr-comment-body", { hasText: "Addressed in the latest push." }),
  ).toBeVisible({
    timeout: 10_000,
  });

  // Add a brand-new comment from the toolbar → it appears as its own thread. Submit with Ctrl/Cmd+Enter (the
  // composer's shortcut) so a transient toast over the button can't intercept the click.
  await page.locator(".weavie-inline-comment").click();
  const composer = page.locator(".weavie-pr-thread-new");
  await composer.locator(".weavie-pr-composer-input").fill("Nit: keep the period.");
  await composer.locator(".weavie-pr-composer-input").press("ControlOrMeta+Enter");
  await expect(
    page.locator(".weavie-pr-comment-body", { hasText: "Nit: keep the period." }),
  ).toBeVisible({
    timeout: 10_000,
  });
});
