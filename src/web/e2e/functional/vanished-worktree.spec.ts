import { rm } from "node:fs/promises";
import { activeSessionSlot, awaitEditorReady, createSession } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

// A session's working directory deleted outside Weavie (a worktree removed in a terminal) ends that session.
// HostCoreVanishedWorktreeTests pins the close at the host seam; this pins the leg only a real page can show:
// the notice the user reads, and that a reconnect no longer replays the observer's raw git words — the bug was
// a dead slot re-erroring on every connect. Transport-agnostic (HostCore owns it), so headless only.
//
// 2026-09-13 05:21 UTC: failed on two platforms in the same run —
// https://github.com/Kapps/weavie/actions/runs/34738041881
//   - macOS (e2e shard 5/6): `chips` never converged to 1 within the 30s timeout. Root cause: the non-Linux
//     directory watch (RecursiveWorkspaceDirectoryWatchSet) is rooted directly at the worktree it watches, and
//     a FileSystemWatcher does not reliably report that exact directory's own deletion (macOS FSEvents can
//     drop the stream with no further event at all). No event meant SignalRefresh() never fired, so the
//     session could sit "vanished" forever, not just past 30s. Fixed in WorkspaceDirectoryWatchSet.cs by also
//     watching the parent directory for this one entry's removal, mirroring Linux's IN_DELETE_SELF handling
//     (see RecursiveWorkspaceDirectoryWatchSetTests.cs).
//   - Windows (e2e shard 6/6): the `rm(worktree, ...)` call below itself threw
//     `EBUSY: resource busy or locked, rmdir '...'`, before any Weavie code ran. Root cause: the forked
//     session's live "claude" PTY is launched with the worktree as its literal Win32 current directory
//     (WindowsConPtyTerminal's CreateProcess `lpCurrentDirectory`), and Windows will not let anything —
//     Weavie's own code included (see WorktreeManager's documented "brief Windows file lock" retries, which
//     only apply once Weavie has already torn its own child down first) — remove a directory that is a live
//     process's current directory. This is a real, deterministic OS constraint, not a transient race: as long
//     as the forked session stays loaded, an external actor (a user in Explorer, this test) cannot fully
//     remove its worktree on Windows. Making this scenario reproducible needs a design decision (e.g. not
//     literally cwd-ing the agent PTY into the worktree on Windows) beyond a CI-triage fix — left for the repo
//     owner; not patched with a retry/timeout here since the lock does not clear on its own while the session
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
