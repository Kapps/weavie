import { expect, test } from "../harness/fixtures";

// Guards issue #135: terminal paste on a browser-served shell (headless/remote) must read the BROWSER's
// clipboard (navigator.clipboard), not the host's. The headless host's clipboard is a no-op that returns
// "", so before the fix a paste silently did nothing — the web round-tripped to the host, got "", and the
// `text.length > 0` guard dropped it. Now `isBrowserHostedShell()` routes copy/paste through
// navigator.clipboard instead. The regression this pins is in our code (the browser-served clipboard path),
// not the model.
//
// Reads the live xterm buffer via the e2e-exposed window.__WEAVIE_TERMINALS__ (slot:pane keyed) — the shell
// echoes pasted input at its prompt, so the sentinel landing in the buffer proves the paste reached the PTY.
test("paste on a browser-served shell inserts the browser clipboard's text", async ({ page }) => {
  const SENTINEL = "ECHO_PASTE_135";
  await page
    .context()
    .grantPermissions(["clipboard-read", "clipboard-write"], { origin: page.url() });

  // This host is browser-served, so the fix's gate (isBrowserHostedShell) is on.
  await expect
    .poll(() => page.evaluate(() => window.__WEAVIE_TERMINALS__ !== undefined))
    .toBe(true);

  // Seed the browser clipboard with the sentinel.
  await page.evaluate((text) => navigator.clipboard.writeText(text), SENTINEL);

  // Focus the shell terminal by its head, then paste with the real keybinding (Ctrl+Shift+V).
  const shell = page.locator('.terminal-surface[data-kind="terminal:shell"]');
  await shell.locator(".pane-head").click();
  await expect(shell).toHaveClass(/\bactive\b/);

  const shellBuffer = (): Promise<string> =>
    page.evaluate(() => {
      const terms = window.__WEAVIE_TERMINALS__ ?? {};
      const key = Object.keys(terms).find((k) => k.endsWith(":shell"));
      if (key === undefined) {
        return "";
      }
      const buf = terms[key].buffer.active;
      const lines: string[] = [];
      for (let i = 0; i < buf.length; i++) {
        lines.push(buf.getLine(i)?.translateToString(true) ?? "");
      }
      return lines.join("\n");
    });

  // Absent before paste; present after — proving the browser clipboard was read and inserted (no longer a
  // silent no-op against the empty host clipboard).
  expect(await shellBuffer()).not.toContain(SENTINEL);
  await page.keyboard.press("Control+Shift+V");
  await expect.poll(shellBuffer, { timeout: 8000 }).toContain(SENTINEL);
});
