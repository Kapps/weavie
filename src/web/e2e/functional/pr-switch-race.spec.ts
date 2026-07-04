import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { awaitReviewSet, collectChangedFiles } from "../harness/navigator";

// Race probe: each PR session carries its own ref-seeded change tracker, and a switch-in re-surfaces the active
// session's review synchronously from that persisted tracker (no per-switch git diff to race). A rapid
// PR->PR->PR switch must therefore settle on only the active PR's files — no stale set from the other PR lingers.
test.use({ prScenario: true });

const toolbar = ".weavie-inline-toolbar";
const chips = ".session-chip";

async function openPrByNumber(
  page: import("@playwright/test").Page,
  n: number,
  expectedChips: number,
): Promise<void> {
  await runCommand(page, "Open Pull Request");
  await expect(page.locator(".session-prompt")).toBeVisible();
  await page.locator(".session-prompt-input").fill(`#${n}`);
  await expect(page.locator(".pr-suggestion-number", { hasText: `#${n}` })).toBeVisible();
  await page.locator(".session-prompt-input").press("Enter");
  await expect(page.locator(chips)).toHaveCount(expectedChips, { timeout: 25_000 });
  await expect(page.locator(toolbar)).toBeVisible({ timeout: 20_000 });
}

test("S2-race: rapid PR->PR->PR switching never leaves the wrong PR's files on screen", async ({
  page,
}) => {
  // Heavyweight: two real PR worktrees plus a rapid switch storm. On the slow, serialized hosted Windows/macOS
  // runners the legitimate work (two branch checkouts + the switches) outlasts the 30s default even with no
  // contention, so give it the room — this marks the test slow, it does not retry it.
  test.slow();
  await openPrByNumber(page, 101, 2); // chip[1]
  await openPrByNumber(page, 102, 3); // chip[2]

  // Rapid-fire switches with no settle in between — chip[1]=#101, chip[2]=#102.
  for (let i = 0; i < 4; i++) {
    await page.locator(chips).nth(1).click();
    await page.locator(chips).nth(2).click();
    await page.locator(chips).nth(1).click();
  }
  // We end on #101 (chip[1]). Settle on the review SET, not just the toolbar or the navigator label: the
  // storm's per-switch pushes settle asynchronously, so the label can already read a #101 file while the set
  // is still mid-swap from #102 — walking then records a transient #102 file (the historical flake).
  await expect(page.locator(chips).nth(1)).toHaveClass(/\bactive\b/);
  await expect(page.locator(toolbar)).toBeVisible({ timeout: 15_000 });
  await awaitReviewSet(page, ["feature.ts", "hello.ts"]);

  // With the set settled, the ← / → walk must surface ONLY #101's files (exact set ⇒ no leak from #102).
  expect([...(await collectChangedFiles(page))].sort()).toEqual(["feature.ts", "hello.ts"]);
});
