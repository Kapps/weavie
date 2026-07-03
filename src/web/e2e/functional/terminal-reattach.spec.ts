import { expect, test } from "../harness/fixtures";

// A client xterm that mounts onto an already-running claude (page reload, background-loaded session viewed
// later) has missed the modes claude set at startup — most visibly `?1049h`, without which the fullscreen TUI
// renders into the normal buffer and grows phantom scrollback (the claude-pane scrollbar bug). The host must
// replay latched modes to the reattaching client (TerminalController.OnReady). Pane remount logic is
// transport-irrelevant, so this runs on headless only.

// Enter the alt screen, hide the cursor, enable bracketed paste, set a title — then stay alive as a healthy
// long-running TUI so the reload below reattaches to the same live child.
test.use({
  fakeScript: {
    steps: [
      {
        op: "print",
        text: "\u001b]0;fake tui\u0007\u001b[?1049h\u001b[?25l\u001b[?2004h✳ fake fullscreen",
      },
      { op: "sleep", ms: 600_000 },
    ],
  },
});

function claudeBufferType(page: import("@playwright/test").Page): Promise<string | null> {
  return page.evaluate(() => {
    const entry = Object.entries(window.__WEAVIE_TERMINALS__ ?? {}).find(([key]) =>
      key.endsWith(":claude"),
    );
    return entry ? entry[1].buffer.active.type : null;
  });
}

test("a reloaded page's claude pane re-enters the alt screen", async ({ page }) => {
  // The live child put the first client's xterm into the alt buffer.
  await expect.poll(() => claudeBufferType(page), { timeout: 20_000 }).toBe("alternate");

  // Reload: a fresh xterm mounts onto the same live child — the exact reattach the bug lives in. Without the
  // mode replay it stays "normal" (claude never re-emits ?1049h) and the TUI leaks into scrollback.
  await page.reload({ waitUntil: "domcontentloaded" });
  await expect.poll(() => claudeBufferType(page), { timeout: 20_000 }).toBe("alternate");
});
