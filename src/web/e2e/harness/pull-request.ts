import { expect, type Locator, type Page } from "@playwright/test";
import { runCommand } from "./actions";
import { walkToChangedFile } from "./navigator";

/** The PR thread card showing `text`. */
export const prCard = (page: Page, text: string): Locator =>
  page.locator(".weavie-pr-card").filter({ hasText: text });

/** Opens the `prScenario` PR #101 and walks its review to hello.ts, whose seeded thread is showing. */
export async function openCommentedFile(page: Page): Promise<void> {
  await runCommand(page, "Open Pull Request…");
  await expect(page.locator(".pr-suggestion-number", { hasText: "#101" })).toBeVisible();
  await page.locator(".session-prompt-input").press("Enter");
  await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 20_000 });
  await walkToChangedFile(page, "hello.ts");
  await expect(prCard(page, "Why change this greeting?")).toBeVisible({ timeout: 10_000 });
}
