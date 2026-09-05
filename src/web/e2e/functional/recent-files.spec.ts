import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { normalizePath } from "../../src/editor/fs-path";
import { activeSessionSlot, awaitEditorReady, createSession, openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

const ALPHA_ONLY = "ALPHA_MAIN_ONLY_CANARY.txt";

test("recent files include only paths present in the selected session @cross", async ({
  page,
  weavie,
}) => {
  test.slow();
  await awaitEditorReady(page);
  const primarySlot = await activeSessionSlot(page);

  // Create B before the A-only file exists so its worktree stays genuinely divergent.
  await createSession(page, { branch: "e2e/recent-files-session", provider: "claude" });
  const betaSlot = await activeSessionSlot(page);
  const [betaRoot] = sessionWorktrees(weavie.workspace);
  if (betaRoot === undefined) {
    throw new Error("The second session did not create a worktree.");
  }

  await writeFile(join(weavie.workspace, ALPHA_ONLY), "Only the primary checkout has this file.\n");
  await page.locator(`.session-chip[data-session-slot="${primarySlot}"]`).click();
  await expect(
    page.locator(`.session-chip.active[data-session-slot="${primarySlot}"]`),
  ).toBeVisible();
  await openFile(page, ALPHA_ONLY);

  const recent = page.locator(".footer-recent-toggle");
  await recent.click();
  await expect(page.locator(".recent-row", { hasText: ALPHA_ONLY })).toBeVisible();
  await page.keyboard.press("Escape");

  await openFile(page, "README.md");
  await recent.click();
  await expect(page.locator(".recent-row", { hasText: ALPHA_ONLY })).toBeVisible();
  await expect(page.locator(".recent-row", { hasText: "README.md" })).toBeVisible();
  await page.keyboard.press("Escape");

  await page.locator(`.session-chip[data-session-slot="${betaSlot}"]`).click();
  await expect(page.locator(`.session-chip.active[data-session-slot="${betaSlot}"]`)).toBeVisible();
  await recent.click();
  const betaReadme = page.locator(".recent-row", { hasText: "README.md" });
  await expect(betaReadme).toBeVisible();
  await expect(page.locator(".recent-row", { hasText: ALPHA_ONLY })).toHaveCount(0);
  await betaReadme.click();
  await expect
    .poll(async () =>
      normalizePath((await page.locator(".editor").getAttribute("data-active-file")) ?? ""),
    )
    .toBe(normalizePath(join(betaRoot, "README.md")));
  await expect(page.locator(".toast", { hasText: "Couldn't open" })).toHaveCount(0);

  await page.locator(`.session-chip[data-session-slot="${primarySlot}"]`).click();
  await expect(
    page.locator(`.session-chip.active[data-session-slot="${primarySlot}"]`),
  ).toBeVisible();
  await recent.click();
  await expect(page.locator(".recent-row", { hasText: ALPHA_ONLY })).toBeVisible();
});
