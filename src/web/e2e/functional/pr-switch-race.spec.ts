import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Race probe: PushActivePrChanges() fires PushPrChangesAsync() fire-and-forget (awaits a git diff), and the web's
// setPrReview has NO active-session/number guard. A rapid PR->PR->PR switch could let a stale pr-changes for the
// wrong PR land on the now-active PR session. (HostCore.PullRequests.cs:311 PushActivePrChanges /
// editor-controller.ts:911 setPrReview.)
test.use({ prScenario: true });

const toolbar = ".weavie-inline-toolbar";
const chips = ".session-chip";
const stackName = ".weavie-inline-stack-name";

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

async function navigatorFiles(page: import("@playwright/test").Page): Promise<Set<string>> {
  const label = page.locator(stackName);
  await page.locator(".monaco-editor").first().click();
  const seen = new Set<string>();
  for (let i = 0; i < 6; i++) {
    const name = (await label.textContent())?.trim();
    if (name) {
      seen.add(name);
    }
    await page.keyboard.press("ControlOrMeta+ArrowRight");
    await page.waitForTimeout(450);
  }
  return seen;
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
  // We end on #101 (chip[1]). Let everything settle (any in-flight async pr-changes arrive).
  await page.waitForTimeout(2500);
  await expect(page.locator(toolbar)).toBeVisible({ timeout: 15_000 });

  // The settled navigator on #101 must contain ONLY #101's files.
  const files = await navigatorFiles(page);
  expect(files.has("widget.ts")).toBe(false);
  expect(files.has("notes.txt")).toBe(false);
  expect([...files].sort()).toEqual(["feature.ts", "hello.ts"]);
});
