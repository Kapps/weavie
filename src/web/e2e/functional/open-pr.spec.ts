import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { focusEditor, navChord, walkToChangedFile } from "../harness/navigator";

// The Open-PR journey end to end against a stubbed PR provider + a local "origin" with a base/head diff
// (prScenario): pick the PR, check out its branch as a session, and walk its base→head diff in the inline-diff
// navigator. See docs/specs/open-pr.md (Phase 2).
test.use({ prScenario: true });

test("File → Open Pull Request checks out its branch and pops up the diff navigator", async ({
  page,
}) => {
  // The repo starts with one session on the workspace checkout.
  await expect(page.locator(".session-chip")).toHaveCount(1);

  await page.getByRole("menuitem", { name: "File", exact: true }).click();
  await expect(page.getByRole("menuitem", { name: /^Open Current Pull Request/ })).toBeDisabled();
  const openPr = page.getByRole("menuitem", { name: /^Open Pull Request…/ });
  const modifier = process.platform === "darwin" ? "⌘" : "Ctrl";
  await expect(openPr.locator(".context-menu-keys")).toHaveText(`${modifier}+Shift+R`);
  await openPr.click();
  await expect(page.locator(".session-prompt")).toBeVisible();
  await expect(page.locator(".pr-suggestion-number", { hasText: "#101" })).toBeVisible();

  // Pick it (Enter on the highlighted row) → a busy spinner toast names the PR while the host does its silent
  // fetch/checkout/seed chain, then a second session lands on the rail, on the PR's head branch.
  await page.locator(".session-prompt-input").press("Enter");
  await expect(page.locator(".toast-busy", { hasText: "#101" })).toBeVisible();
  await expect(page.locator(".session-chip")).toHaveCount(2, { timeout: 20_000 });

  // The diff navigator surfaces automatically on the PR's first changed file — feature.ts, a new file, so it
  // shows the "New file" band rather than a per-line green wash.
  const toolbar = page.locator(".weavie-inline-toolbar");
  await expect(toolbar).toBeVisible({ timeout: 20_000 });

  // The correlated open result clears the spinner after the exact PR session has been selected.
  await expect(page.locator(".toast-busy")).toHaveCount(0, { timeout: 20_000 });
  await expect(page.locator(".weavie-inline-newfile-tag")).toBeVisible();

  // Review Changes hydrates the complete file list while the file tab retains its own state.
  await page.locator(".editor-review-open").click();
  const overview = page.locator(".unified-review");
  await expect(overview.locator(".unified-review-file")).toHaveCount(2);
  await expect(
    overview.locator(".unified-review-file-name", { hasText: "feature.ts" }),
  ).toBeVisible();
  await expect(
    overview.locator(".unified-review-file-name", { hasText: "hello.ts" }),
  ).toBeVisible();
  await expect(overview.locator(".unified-review-notice", { hasText: "Loading" })).toHaveCount(0, {
    timeout: 20_000,
  });
  await overview
    .locator(".unified-review-file", { hasText: "feature.ts" })
    .locator(".line-numbers")
    .first()
    .click();
  await expect(overview).toHaveCount(0);
  await expect(page.locator(".editor-tab", { hasText: "Review Changes" })).toBeVisible();
  await expect(toolbar).toBeVisible();

  // It's a two-file walk (feature.ts added, hello.ts modified): the stacked label names the current file and
  // ← / → moves between them.
  const label = page.locator(".weavie-inline-stack-name");
  await expect(label).toHaveText(/feature\.ts|hello\.ts/);
  const first = (await label.textContent())?.trim() ?? "";
  await focusEditor(page); // the ← / → chord is `!terminalFocused`-guarded; a switch-in focuses Claude's terminal
  await page.keyboard.press(navChord("ArrowRight"));
  await expect
    .poll(async () => (await label.textContent())?.trim(), { timeout: 10_000 })
    .not.toBe(first);
  // The modified file (hello.ts) still shows the per-line added wash.
  await expect(page.locator(".weavie-inline-added").first()).toBeVisible();

  await page.getByRole("menuitem", { name: "File", exact: true }).click();
  await expect(page.getByRole("menuitem", { name: /^Open Current Pull Request/ })).toBeEnabled();
  await page.keyboard.press("Escape");
  await page.getByRole("menuitem", { name: "Diff", exact: true }).click();
  await page.getByRole("menuitem", { name: /^Close Diff/ }).click();
  await expect(toolbar).toBeHidden();
});

test("typing #N opens a PR directly by number", async ({ page }) => {
  await runCommand(page, "Open Pull Request…");
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

test("a PR's review comments render on its changed file", async ({ page }) => {
  await runCommand(page, "Open Pull Request…");
  await expect(page.locator(".pr-suggestion-number", { hasText: "#101" })).toBeVisible();
  await page.locator(".session-prompt-input").press("Enter");
  await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 20_000 });
  await walkToChangedFile(page, "hello.ts");
  await expect(
    page.locator(".weavie-pr-comment-body", { hasText: "Why change this greeting?" }),
  ).toBeVisible({ timeout: 10_000 });
});
