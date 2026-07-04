import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { focusEditor, navChord, walkToChangedFile } from "../harness/navigator";

// Probes the PR-review surface across SESSION SWITCHES (the suspected bug nest). The prScenario fixture stubs
// one PR (#101: feature.ts added + hello.ts modified) over a local "origin", with a seeded comment on hello.ts.
test.use({ prScenario: true });

const toolbar = ".weavie-inline-toolbar";
const added = ".weavie-inline-added";
const chips = ".session-chip";

async function openPr101(page: import("@playwright/test").Page): Promise<void> {
  await runCommand(page, "Open Pull Request");
  await expect(page.locator(".pr-suggestion-number", { hasText: "#101" })).toBeVisible();
  await page.locator(".session-prompt-input").press("Enter");
  await expect(page.locator(chips)).toHaveCount(2, { timeout: 20_000 });
  await expect(page.locator(toolbar)).toBeVisible({ timeout: 20_000 });
}

// SCENARIO 1: PR session -> non-PR session -> back. Does the navigator/toolbar disappear on the non-PR session
// and re-appear on switch-back?
test("S1: navigator disappears on switch to non-PR session and returns on switch back", async ({
  page,
}) => {
  await openPr101(page);
  await expect(page.locator(added).first()).toBeVisible();

  // The two chips: the PR session is the second (just-opened, active). The primary is the first.
  const primaryChip = page.locator(chips).first();
  const prChip = page.locator(chips).nth(1);

  // Switch to the primary (non-PR) session.
  await primaryChip.click();
  // EXPECTED: the PR diff navigator/toolbar should go away on a non-PR session.
  await expect(page.locator(toolbar)).toHaveCount(0, { timeout: 10_000 });

  // Switch back to the PR session.
  await prChip.click();
  // EXPECTED: the navigator/toolbar re-appears for the PR session.
  await expect(page.locator(toolbar)).toBeVisible({ timeout: 10_000 });
  await expect(page.locator(added).first()).toBeVisible();
});

// SCENARIO 4: comment after switching away and back — must post against the correct PR and refresh the thread.
test("S4: replying to a PR comment after a round-trip switch still posts and refreshes", async ({
  page,
}) => {
  await openPr101(page);

  // Walk to hello.ts (the commented file).
  await walkToChangedFile(page, "hello.ts");
  await expect(
    page.locator(".weavie-pr-comment-body", { hasText: "Why change this greeting?" }),
  ).toBeVisible({ timeout: 10_000 });

  // Switch away to primary and back to the PR.
  await page.locator(chips).first().click();
  await expect(page.locator(toolbar)).toHaveCount(0, { timeout: 10_000 });
  await page.locator(chips).nth(1).click();
  await expect(page.locator(toolbar)).toBeVisible({ timeout: 10_000 });

  // Re-open hello.ts diff (navigator may have re-armed on feature.ts).
  await walkToChangedFile(page, "hello.ts");

  // Reply in the thread → it must round-trip through the (correct) PR's comment store.
  const thread = page.locator(".weavie-pr-thread").first();
  await thread.locator(".weavie-pr-composer-input").fill("Reply after switching back.");
  await thread.locator(".weavie-pr-composer-submit").click();
  await expect(
    page.locator(".weavie-pr-comment-body", { hasText: "Reply after switching back." }),
  ).toBeVisible({ timeout: 10_000 });
});

// SCENARIO 3: in-flight get-turn-diff during a switch. Open a file's diff, then switch sessions fast; a stale
// per-file diff for the wrong session must not render onto the new session's editor.
test("S3: a stale per-file diff cannot render onto a non-PR session after a quick switch", async ({
  page,
}) => {
  await openPr101(page);
  await expect(page.locator(added).first()).toBeVisible();

  // can reply. Stepping the file walk issues get-turn-diff for the neighbour.
  await focusEditor(page); // confirm the editor holds focus so the `!terminalFocused`-guarded chord lands
  await page.keyboard.press(navChord("ArrowRight"));
  await page.locator(chips).first().click();

  // The non-PR session must show NO diff surface — no toolbar, no bright added band leaking over it.
  await expect(page.locator(toolbar)).toHaveCount(0, { timeout: 10_000 });
  await expect(page.locator(added)).toHaveCount(0, { timeout: 10_000 });

  // Anchor the "stale diff never lands" check on a real later event instead of a fixed delay: open a file on
  // the primary session and wait for its content to render. That round-trip is slower than the diff the
  // pre-switch get-turn-diff awaited, so by the time it paints the stale reply has already had its chance — and
  // the surface must still be absent (the host drops it: get-turn-diff reads the now-active session's tracker).
  await page.locator(".tb-omnibar-input").click();
  await page.locator(".tb-omnibar-input").fill("notes.txt");
  await expect(page.locator(".tb-omnibar-row", { hasText: "notes.txt" }).first()).toBeVisible();
  await page.locator(".tb-omnibar-input").press("Enter");
  await expect(page.locator(".monaco-editor .view-lines")).toContainText("just plain text");
  await expect(page.locator(toolbar)).toHaveCount(0);
  await expect(page.locator(added)).toHaveCount(0);
});

// SCENARIO 5: navigator "parked" over an unrelated file (none of the changed PR files in view), then switch.
// No stale parked navigator from the prior PR should remain on the non-PR session.
test("S5: a parked navigator does not linger after switching to a non-PR session", async ({
  page,
}) => {
  await openPr101(page);
  await expect(page.locator(added).first()).toBeVisible();

  // Open an unrelated, unchanged file (notes.txt) so the navigator parks (no changed file in view) rather
  // than rendering a live diff. The toolbar stays (parked), but over an unchanged buffer.
  await page.locator(".tb-omnibar-input").click();
  await page.locator(".tb-omnibar-input").fill("notes.txt");
  await expect(page.locator(".tb-omnibar-row", { hasText: "notes.txt" }).first()).toBeVisible();
  await page.locator(".tb-omnibar-input").press("Enter");
  await expect(page.locator(".editor-tab", { hasText: "notes.txt" })).toBeVisible();

  // Switch to the non-PR primary — the parked navigator must clear.
  await page.locator(chips).first().click();
  await expect(page.locator(toolbar)).toHaveCount(0, { timeout: 10_000 });
});

// SCENARIO 4b: try to comment WHILE the non-PR session is active (the navigator should be gone, but if it
// lingers, ActiveReview() has changed so the post would be silently dropped). This documents whether the
// surface is even reachable from the wrong session.
test("S4b: PR comment surface is not reachable while a non-PR session is active", async ({
  page,
}) => {
  await openPr101(page);
  // Switch to the non-PR primary.
  await page.locator(chips).first().click();
  // The whole PR comment surface should be gone — no thread, no composer to mis-post from.
  await expect(page.locator(".weavie-pr-thread")).toHaveCount(0, { timeout: 10_000 });
  await expect(page.locator(toolbar)).toHaveCount(0, { timeout: 10_000 });
});
