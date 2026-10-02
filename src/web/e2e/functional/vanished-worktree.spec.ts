import { rename, symlink, unlink, writeFile } from "node:fs/promises";
import { join, normalize } from "node:path";
import { activeSessionSlot, awaitEditorReady, createSession, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

// HostCoreVanishedWorktreeTests covers detection; this checks the user-facing notice and reconnect replay.
const RAW_OBSERVER_ERRORS = /Git working directory does not exist|Couldn't load workspace files/;

test("a worktree deleted outside Weavie closes its session, for good", async ({ page, weavie }) => {
  const chips = page.locator(".session-chip");
  const workspaceSlot = await activeSessionSlot(page);
  await createSession(page, { branch: "e2e/vanished-worktree", provider: "claude" });
  await expect(chips).toHaveCount(2);
  await runCommand(page, "Unload Session");
  await expect(page.locator(".session-chip.unloaded")).toHaveCount(1);
  const [worktree] = sessionWorktrees(weavie.workspace);
  if (worktree === undefined) throw new Error("The session did not create a git worktree");

  // Remove the configured path without deleting the directory held open by Windows child processes.
  const backing = join(weavie.home, "vanished-worktree-backing");
  await rename(worktree, backing);
  await symlink(backing, worktree, "junction");
  const beforeReload = weavie.log().length;
  await page.locator(".session-chip.unloaded").click();
  await expect(page.locator(".session-chip.unloaded")).toHaveCount(0);
  await expect
    .poll(() => weavie.log().slice(beforeReload))
    .toContain(`workspace watcher on ${normalize(worktree)}`);
  await unlink(worktree);
  // Linux watches the backing directory; its next invalidation must observe the missing session root.
  await writeFile(join(backing, "observer-invalidation"), "external change");

  // The session ends itself — no command, no click — leaving the workspace session selected.
  await expect(chips).toHaveCount(1);
  await expect(page.locator(".session-chip.active")).toHaveAttribute(
    "data-session-slot",
    workspaceSlot,
  );
  await expect(page.locator(".toast .toast-msg", { hasText: "no longer exists" })).toHaveText(
    `Session 'e2e/vanished-worktree' was closed: its worktree ${normalize(worktree)} no longer exists.`,
  );
  await expect(page.locator(".toast", { hasText: RAW_OBSERVER_ERRORS })).toHaveCount(0);

  // Reconnect: the editor rebinding proves the fresh connection's replay has run. The raw errors (error-level
  // toasts, which never auto-dismiss) must not be part of it, and the closed session must not reappear.
  await page.reload({ waitUntil: "domcontentloaded" });
  await expect(page.locator("#splash")).toHaveCount(0, { timeout: 40_000 });
  await awaitEditorReady(page);
  await expect(page.locator(".toast", { hasText: RAW_OBSERVER_ERRORS })).toHaveCount(0);
  await expect(chips).toHaveCount(1);
});
