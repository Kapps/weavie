import { rm } from "node:fs/promises";
import { activeSessionSlot, awaitEditorReady, createSession } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

// A session's working directory deleted outside Weavie (a worktree removed in a terminal) ends that session.
// HostCoreVanishedWorktreeTests pins the close at the host seam; this pins the leg only a real page can show:
// the notice the user reads, and that a reconnect no longer replays the observer's raw git words — the bug was
// a dead slot re-erroring on every connect. Transport-agnostic (HostCore owns it), so headless only.
//
// 2026-09-13 05:21 UTC, https://github.com/Kapps/weavie/actions/runs/34738041881 — failed on two platforms:
//   - macOS (shard 5/6): watcher missed the worktree's own deletion (FileSystemWatcher doesn't reliably
//     report a watched directory's own removal), so the session never closed. Fixed: WorkspaceDirectoryWatchSet
//     now also watches the parent for this entry's removal (see RecursiveWorkspaceDirectoryWatchSetTests.cs).
//   - Windows (shard 6/6): `rm(worktree, ...)` itself threw EBUSY — the forked session's live PTY still had
//     the worktree as its OS current directory, which Windows won't let anything remove while alive. Real,
//     non-transient OS constraint, not fixed here: needs a design decision on not cwd-ing the agent PTY into
//     a directory that must stay externally removable while the session runs. Left red pending that call
//     rather than papered over with a retry/timeout (the lock never clears on its own while the session is live).
//     is alive.
const RAW_OBSERVER_ERRORS = /Git working directory does not exist|Couldn't load workspace files/;

test("a worktree deleted outside Weavie closes its session, for good", async ({ page, weavie }) => {
  const chips = page.locator(".session-chip");
  const workspaceSlot = await activeSessionSlot(page);
  await createSession(page, { branch: "e2e/vanished-worktree", provider: "claude" });
  await expect(chips).toHaveCount(2);
  const [worktree] = sessionWorktrees(weavie.workspace);
  if (worktree === undefined) {
    throw new Error("the forked session did not create a git worktree");
  }

  await rm(worktree, { recursive: true, force: true });

  // The session ends itself — no command, no click — leaving the workspace session selected.
  await expect(chips).toHaveCount(1);
  await expect(page.locator(".session-chip.active")).toHaveAttribute(
    "data-session-slot",
    workspaceSlot,
  );
  await expect(page.locator(".toast .toast-msg", { hasText: "no longer exists" })).toHaveText(
    `Session 'e2e/vanished-worktree' was closed: its worktree ${worktree} no longer exists.`,
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
