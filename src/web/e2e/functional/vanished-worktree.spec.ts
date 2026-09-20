import { readFile, rm, writeFile } from "node:fs/promises";
import { normalize } from "node:path";
import type { WebSocket } from "@playwright/test";
import { activeSessionSlot, awaitEditorReady, createSession } from "../harness/actions";
import { writeFakeClaudeWrapper } from "../harness/fake-claude";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";
import { decodeTestWebSocketMessage } from "../harness/websocket-codec";

// A session's working directory deleted outside Weavie (a worktree removed in a terminal) ends that session.
// HostCoreVanishedWorktreeTests pins the close at the host seam; this pins the leg only a real page can show:
// the notice the user reads, and that a reconnect no longer replays the observer's raw git words — the bug was
// a dead slot re-erroring on every connect. Transport-agnostic (HostCore owns it), so headless only.
const RAW_OBSERVER_ERRORS = /Git working directory does not exist|Couldn't load workspace files/;

let socket: WebSocket;
test.use({
  preNavigate: {
    run: async (page) => {
      page.on("websocket", (connected) => {
        socket = connected;
      });
    },
  },
});

test("a worktree deleted outside Weavie closes its session, for good", async ({ page, weavie }) => {
  // Keep the inert test agent outside the checkout so Windows permits external deletion.
  const wrapper = await writeFakeClaudeWrapper(weavie.home);
  const launch = await readFile(wrapper, "utf8");
  await writeFile(
    wrapper,
    process.platform === "win32"
      ? `@cd /d "${weavie.home}"\r\n${launch}`
      : launch.replace("exec ", `cd '${weavie.home.replaceAll("'", "'\\''")}'\nexec `),
  );
  const chips = page.locator(".session-chip");
  const workspaceSlot = await activeSessionSlot(page);
  await createSession(page, { branch: "e2e/vanished-worktree", provider: "claude" });
  await expect(chips).toHaveCount(2);
  const shell = page.locator('.terminal-surface[data-kind="terminal:shell"]');
  await shell.locator(".shell-tab-main").click();
  let closeRequest: string;
  socket.on("framesent", ({ payload }) => {
    const message = JSON.parse(decodeTestWebSocketMessage(payload));
    if (
      message.kind === "request" &&
      message.feature === "commands" &&
      message.payload?.id === "weavie.terminal.close"
    )
      closeRequest = message.requestId;
  });
  const closed = socket.waitForEvent("framereceived", ({ payload }) => {
    const message = JSON.parse(decodeTestWebSocketMessage(payload));
    return (
      message.kind === "response" &&
      message.requestId === closeRequest &&
      message.payload?.ok === true
    );
  });
  await page.keyboard.press("ControlOrMeta+Shift+W");
  await closed;
  await expect(shell.locator(".shell-tab")).toHaveCount(0);
  const [worktree] = sessionWorktrees(weavie.workspace);
  if (worktree === undefined) {
    throw new Error("the forked session did not create a git worktree");
  }

  // Flake, 2026-09-20 05:41 UTC: https://github.com/Kapps/weavie/actions/runs/35491607862/job/106027946640
  // Windows shard 6/6 hit `EBUSY: resource busy or locked, rmdir ...worktrees\e2e-vanished-worktree`. Root
  // cause was in the host, not this test: WindowsConPtyTerminal.RaiseExited() waited on the shell process
  // handle with a fixed 2s timeout and ignored the result, so `weavie.terminal.close` (awaited above) could
  // report success before Windows had actually released the process's handle to its working directory
  // inside the worktree. Fixed by waiting without a timeout, since the owning job object's
  // KILL_ON_JOB_CLOSE already guarantees the process is terminating by the time this wait runs
  // (src/Weavie.Core/Terminal/WindowsConPtyTerminal.cs).
  await rm(worktree, { recursive: true, force: true });

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
