import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Session lifecycle through the rail + commands: create (fork a worktree session off the current one),
// switch, unload, and reopen. Sessions and worktrees are a HostCore concern that differs structurally on
// remote (the Runner provisions the worktree), so this runs @cross.
test("create, switch, unload, and reopen sessions @cross", async ({ page }) => {
  const chips = page.locator(".session-chip");
  await expect(chips).toHaveCount(1);

  // Create: forking spins up a second session on its own worktree, which becomes active.
  await runCommand(page, "Fork Session");
  await expect(chips).toHaveCount(2);
  await expect(page.locator(".session-chip.active")).toHaveCount(1);

  // Switch: clicking the first chip makes it active.
  await chips.first().click();
  await expect(chips.first()).toHaveClass(/\bactive\b/);

  // Switch via the keyboard (Next Session) — still exactly one active session.
  await runCommand(page, "Next Session");
  await expect(page.locator(".session-chip.active")).toHaveCount(1);

  // Unload: the active session's backend is torn down; its chip goes faded/unloaded.
  await runCommand(page, "Unload Session");
  await expect(page.locator(".session-chip.unloaded")).toHaveCount(1);

  // Reopen: clicking an unloaded chip loads it again (no longer unloaded).
  await page.locator(".session-chip.unloaded").click();
  await expect(page.locator(".session-chip.unloaded")).toHaveCount(0);
});

// Delete needs the confirmation flow behind "Delete Session…", which doesn't surface as the omnibar prompt
// the way the other commands do. Left for a follow-up once the delete-confirm affordance is pinned down.
test.fixme("delete a session removes its chip @cross", async ({ page }) => {
  const chips = page.locator(".session-chip");
  await runCommand(page, "Fork Session");
  await expect(chips).toHaveCount(2);
  await runCommand(page, "Delete Session");
  await expect(chips).toHaveCount(1);
});
