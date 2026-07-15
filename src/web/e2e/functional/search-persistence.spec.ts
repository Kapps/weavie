import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { awaitEditorReady } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// The find-in-files panel persists its match mode, include/exclude globs, and recent-terms history host-side
// (~/.weavie/search-state.json), but never the search term. These journeys prove the round-trip across a page
// reload plus the Alt+Up history recall and the exclude-gitignored toggle. Real git grep over the seeded
// workspace — deterministic, no claude involvement.

async function openSearch(page: Page): Promise<void> {
  await expect(async () => {
    await page.keyboard.press("ControlOrMeta+Shift+f");
    await expect(page.locator(".search-panel")).toBeVisible({ timeout: 1000 });
  }).toPass({ timeout: 10_000 });
}

async function reload(page: Page): Promise<void> {
  await page.reload({ waitUntil: "domcontentloaded" });
  await expect(page.locator("#splash")).toHaveCount(0, { timeout: 40_000 });
  await awaitEditorReady(page);
}

test("options, globs, and recent terms persist across a reload — but not the query", async ({
  page,
}) => {
  await awaitEditorReady(page);
  await openSearch(page);
  const input = page.locator(".search-input");
  const caseToggle = page.locator(".search-toggle").nth(0);

  // Turn on Match Case, set an include glob, and run a search that we commit by opening a result.
  await page.keyboard.press("Alt+c");
  await expect(caseToggle).toHaveAttribute("aria-pressed", "true");
  await page.locator(".search-glob").nth(0).fill("*.ts");
  await input.fill("greet");
  await expect(page.locator(".search-row").first()).toBeVisible();
  await page.keyboard.press("Enter"); // commits "greet" into the recent-terms history

  await reload(page);
  await openSearch(page);

  // The query is NOT restored (blank), but the mode and glob are, and the term is recallable from history.
  await expect(input).toHaveValue("");
  await expect(page.locator(".search-toggle").nth(0)).toHaveAttribute("aria-pressed", "true");
  await expect(page.locator(".search-glob").nth(0)).toHaveValue("*.ts");
  await page.keyboard.press("Alt+ArrowUp");
  await expect(input).toHaveValue("greet");
});

test("Alt+G toggles searching gitignored files, advertising its binding", async ({
  page,
  weavie,
}) => {
  // A gitignored file with a unique token: excluded by default (git grep --untracked honors .gitignore),
  // searchable once the toggle is off (--no-exclude-standard).
  const ws = weavie.workspace;
  writeFileSync(join(ws, ".gitignore"), "ignored-zone/\n");
  mkdirSync(join(ws, "ignored-zone"), { recursive: true });
  writeFileSync(
    join(ws, "ignored-zone", "buried.ts"),
    "export const marker = 'zqx-ignored-hit';\n",
  );

  await awaitEditorReady(page);
  await openSearch(page);
  const gitignoreToggle = page.locator(".search-toggle").nth(3);
  await expect(gitignoreToggle).toHaveAttribute("title", /Exclude gitignored files \(Alt\+G\)/);
  await expect(gitignoreToggle).toHaveAttribute("aria-pressed", "true"); // on by default

  await page.locator(".search-input").fill("zqx-ignored-hit");
  await expect(page.locator(".search-empty")).toContainText("No results");

  await page.keyboard.press("Alt+g");
  await expect(gitignoreToggle).toHaveAttribute("aria-pressed", "false");
  await expect(page.locator(".search-group-name").filter({ hasText: "buried.ts" })).toHaveCount(1);
});
