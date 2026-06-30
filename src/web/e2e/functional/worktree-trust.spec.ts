import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

// Regression guard for the worktree workspace-trust write. A freshly-created worktree is an UNTRUSTED
// directory for the embedded claude, so its blocking first-run trust dialog disrupts the ws:// handshake to
// Weavie's IDE + registry servers — leaving a secondary session without openDiff / the mcp__weavie__* tools the
// primary checkout kept. HostCore now pre-accepts that dialog (ClaudeWorkspaceTrust.EnsureTrusted) for each
// Weavie-created worktree before launching claude. The decisive, deterministic proof is the on-disk write: after forking a
// worktree session, $CLAUDE_CONFIG_DIR/.claude.json must record projects[<worktree>].hasTrustDialogAccepted.

function trustedProjects(configDir: string): Record<string, { hasTrustDialogAccepted?: boolean }> {
  const path = join(configDir, ".claude.json");
  return existsSync(path) ? (JSON.parse(readFileSync(path, "utf8")).projects ?? {}) : {};
}

test("a forked worktree session is pre-trusted in claude's config", async ({ page, weavie }) => {
  const chips = page.locator(".session-chip");

  // Fork a worktree-backed session through the rail's "+" (the exact user path), naming a new branch off HEAD.
  await page.locator(".session-rail-add").click();
  await page.locator(".session-prompt-input").fill("trust-check");
  await page.locator(".session-prompt-btn-primary").click();

  // The second chip appearing — and reaching a loaded status (its dot) — is the "claude pane came up alive"
  // signal: the worktree session launched its claude through the PTY.
  await expect(chips).toHaveCount(2);
  await expect(chips.nth(1).locator(".session-chip-dot")).toBeVisible({ timeout: 30_000 });

  // The decisive proof of the fix: the new worktree's path must be recorded trusted in claude's config, so the
  // embedded claude skips the first-run trust dialog that used to sever the Weavie MCP integration.
  const [worktree] = sessionWorktrees(weavie.workspace);
  expect(worktree).toBeTruthy();
  await expect
    .poll(() => trustedProjects(weavie.claudeConfigDir)[worktree]?.hasTrustDialogAccepted, {
      timeout: 30_000,
    })
    .toBe(true);
});
