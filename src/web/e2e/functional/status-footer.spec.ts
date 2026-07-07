import { expect, test } from "../harness/fixtures";

// The single terminal status footer: it lives on the shell pane only (the Claude pane stays chrome-free
// below its TUI) and carries both the workspace git branch and the Claude session status pushed by the
// hook stream. Regression guard for the footer move out of the Claude pane.

const shellFooter = '.terminal-surface[data-kind="terminal:shell"] .pane-footer';

test.describe("terminal status footer", () => {
  // SessionStart then UserPromptSubmit leave the session's terminal state at Working — deterministic to
  // assert (no later transition can race the expectation).
  test.use({
    fakeScript: {
      steps: [
        { op: "hook", request: { hook_event_name: "SessionStart", source: "startup" } },
        { op: "hook", request: { hook_event_name: "UserPromptSubmit" } },
      ],
    },
  });

  test("shell pane owns the one footer: branch + live claude status; claude pane has none", async ({
    page,
  }) => {
    const footer = page.locator(shellFooter);
    // Branch segment: the throwaway workspace is a real git repo, so the ⎇ segment renders its branch.
    await expect(footer.locator(".footer-branch")).toBeVisible();
    // Claude status segment: the hook stream (SessionStart → UserPromptSubmit) lands in the shell footer.
    await expect(footer).toContainText("Working", { timeout: 20_000 });
    // The Claude pane renders no footer of its own — its TUI runs to the pane edge.
    await expect(page.locator('.terminal-surface[data-kind="agent"] .pane-footer')).toHaveCount(0);
    // The editor keeps its own footer (a distinct component, untouched by the terminal footer move).
    await expect(page.locator('.editor-surface[data-kind="editor"] .pane-footer')).toHaveCount(1);
  });
});
