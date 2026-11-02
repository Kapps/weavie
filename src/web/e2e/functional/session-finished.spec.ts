import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { activeSessionSlot, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

// The pinned panel's Finished section (docs/specs/session-attention.md): a turn that completes in a session
// you aren't watching is flagged unseen on the host until you view that session.

// The panel section a row sits under: its nearest preceding group header.
const groupOf = (page: Page, slot: string): Promise<string | null> =>
  page.evaluate((s) => {
    let node = document.querySelector(`.session-panel-row[data-session-slot="${s}"]`);
    while (node !== null && !node.classList.contains("session-panel-group")) {
      node = node.previousElementSibling;
    }
    return node?.textContent ?? null;
  }, slot);

test("a turn finishing in a background session shows under Finished until that session is viewed", async ({
  page,
  weavie,
}) => {
  const home = await activeSessionSlot(page);
  const surface = await createAcpSession(page, "feature/finished-bg");
  const background = await activeSessionSlot(page);
  // Pinned before the turn starts: the palette's Escape would interrupt a running turn.
  await runCommand(page, "Pin Session Panel");
  await submitAcpDraft(surface, "hold");
  await expect(surface.locator(".agent-working")).toBeVisible();

  const row = (slot: string) => page.locator(`.session-panel-row[data-session-slot="${slot}"]`);
  await row(home).click();
  await expect(row(home)).toHaveClass(/\bactive\b/);
  await expect.poll(() => groupOf(page, background)).toBe("Working");

  await writeFile(join(sessionWorktrees(weavie.workspace)[0]!, "release-hold"), "");
  await expect.poll(() => groupOf(page, background)).toBe("Finished");
  await expect(row(background).locator(".session-status")).toHaveClass(/\bstatus-finished\b/);
  await expect(row(background).locator(".session-panel-activity")).toContainText(/^finished · /);

  // The flag is host-owned, so it survives a reload.
  await page.reload();
  await expect.poll(() => groupOf(page, background)).toBe("Finished");

  await row(background).click();
  await expect(row(background)).toHaveClass(/\bactive\b/);
  await expect.poll(() => groupOf(page, background)).toBe("Idle");
  await expect(row(background).locator(".session-status")).toHaveClass(/\bstatus-idle\b/);

  await row(home).click();
  await expect(row(home)).toHaveClass(/\bactive\b/);
  // Viewing cleared the host's flag, not just this page's rendering.
  await page.reload();
  await expect(row(home)).toHaveClass(/\bactive\b/);
  await expect.poll(() => groupOf(page, background)).toBe("Idle");
});

test("a turn finishing in the watched session goes straight to Idle", async ({ page, weavie }) => {
  const surface = await createAcpSession(page, "feature/finished-watched");
  const watched = await activeSessionSlot(page);
  await runCommand(page, "Pin Session Panel");
  await submitAcpDraft(surface, "hold");
  await expect.poll(() => groupOf(page, watched)).toBe("Working");
  // Records any frame in which the watched row read Finished, however briefly.
  await page.evaluate(() => {
    const seen = window as unknown as { __flashed: boolean };
    seen.__flashed = false;
    new MutationObserver(() => {
      if (document.querySelector(".session-panel-row.active .status-finished") !== null) {
        seen.__flashed = true;
      }
    }).observe(document.body, { subtree: true, childList: true, attributes: true });
  });

  await writeFile(join(sessionWorktrees(weavie.workspace)[0]!, "release-hold"), "");
  await expect(surface.locator(".agent-working")).toHaveCount(0);
  await expect.poll(() => groupOf(page, watched)).toBe("Idle");
  expect(await page.evaluate(() => (window as unknown as { __flashed: boolean }).__flashed)).toBe(
    false,
  );
  await expect(page.locator(".session-panel-group", { hasText: "Finished" })).toHaveCount(0);
});
