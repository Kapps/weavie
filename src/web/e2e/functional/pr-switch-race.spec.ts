import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { collectChangedFiles } from "../harness/navigator";

// Race probe: PushActivePrChanges() fires PushPrChangesAsync() fire-and-forget (awaits a git diff), and a rapid
// PR->PR->PR switch could let a stale pr-changes for the wrong PR land on the now-active PR session. The active
// session resolves the review, so the settled navigator must carry only the active PR's files.
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
  await openPrByNumber(page, 101, 2); // chip[1]
  await openPrByNumber(page, 102, 3); // chip[2]

  // Rapid-fire switches with no settle in between — chip[1]=#101, chip[2]=#102.
  for (let i = 0; i < 4; i++) {
    await page.locator(chips).nth(1).click();
    await page.locator(chips).nth(2).click();
    await page.locator(chips).nth(1).click();
  }
  // We end on #101 (chip[1]). Its toolbar being live is the settle signal — any in-flight pr-changes for the
  // other PR has lost the race by the time we read the navigator below.
  await expect(page.locator(chips).nth(1)).toHaveClass(/\bactive\b/);
  await expect(page.locator(toolbar)).toBeVisible({ timeout: 15_000 });

  // The settled navigator on #101 must contain ONLY #101's files (exact set ⇒ no leak from #102).
  expect([...(await collectChangedFiles(page))].sort()).toEqual(["feature.ts", "hello.ts"]);
});
