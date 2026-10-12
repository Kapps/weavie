import { expect, type Locator, type Page } from "@playwright/test";
import { openPullRequestFile } from "./review";

/** The PR thread card showing `text`. */
export const prCard = (page: Page, text: string): Locator =>
  page.locator(".weavie-pr-card").filter({ hasText: text });

/** Opens the `prScenario` PR #101 (landing on its overview), then hello.ts, whose seeded thread is showing. */
export async function openCommentedFile(page: Page): Promise<void> {
  await openPullRequestFile(page, "hello.ts");
  await expect(prCard(page, "Why change this greeting?")).toBeVisible({ timeout: 10_000 });
}
