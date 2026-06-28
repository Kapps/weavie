import { expect, test } from "../harness/fixtures";

// Regression (#108): opening the command palette moves DOM focus to the omnibar input, which fired focusin
// and reset the focus-context (editorFocused/terminalFocused → false). The palette filtered commands by
// their `when` BEFORE accounting for that, so every focus-gated command was dropped — searching ">copy"
// with a terminal focused returned ZERO rows. The fix evaluates `when` against the pane focused when the
// palette opened. Pure frontend `when`-evaluation against pre-open focus → headless.
test("palette shows terminal-gated Copy/Paste when a terminal was focused before opening", async ({
  page,
}) => {
  const shell = page.locator('.terminal-surface[data-kind="terminal:shell"]');
  const box = page.locator(".tb-omnibar-box");
  const input = page.locator(".tb-omnibar-input");

  // Focus a terminal pane — clicking its head lands DOM focus on the pane's xterm (terminalFocused = true).
  await shell.locator(".pane-head").click();
  await expect(shell).toHaveClass(/\bactive\b/);

  // Open the palette (omnibar command mode); focus now sits in the omnibar input, not the terminal.
  await expect(async () => {
    await page.keyboard.press("ControlOrMeta+Shift+p");
    await expect(box).toHaveClass(/\bopen\b/, { timeout: 1000 });
  }).toPass({ timeout: 10_000 });
  await input.fill(">copy");

  // The terminal-gated Copy command (When="terminalFocused") is visible — proven by both its row and the
  // Terminal category it carries (the file-search "No matching files" path can't produce these).
  const copyRow = page.locator(".tb-omnibar-row", { hasText: "Copy" });
  await expect(copyRow.first()).toBeVisible();
  await expect(copyRow.filter({ has: page.locator(".tb-row-dir", { hasText: "Terminal" }) })).toHaveCount(
    1,
  );

  // Paste is the same gate — confirm it surfaces too.
  await input.fill(">paste");
  await expect(
    page
      .locator(".tb-omnibar-row", { hasText: "Paste" })
      .filter({ has: page.locator(".tb-row-dir", { hasText: "Terminal" }) }),
  ).toHaveCount(1);
});

// Negative half of the same gate: with NO terminal focused (the editor focused instead), the terminal-gated
// Copy must NOT appear — the fix narrows visibility to the pre-open focus, it doesn't unconditionally show
// every command. This is what keeps the fix honest rather than "show everything always".
test("palette hides terminal-gated Copy when the editor was focused before opening", async ({
  page,
}) => {
  const box = page.locator(".tb-omnibar-box");
  const input = page.locator(".tb-omnibar-input");

  // Open a file and click into Monaco so editorFocused (not terminalFocused) is the pre-open focus.
  await input.click();
  await input.fill("hello.ts");
  await expect(page.locator(".tb-omnibar-row", { hasText: "hello.ts" }).first()).toBeVisible();
  await input.press("Enter");
  await expect(page.locator(".editor-tab", { hasText: "hello.ts" })).toBeVisible();
  await page.locator(".monaco-editor .view-lines").first().click();
  await expect(page.locator('.editor-surface[data-kind="editor"]')).toHaveClass(/\bactive\b/);

  await expect(async () => {
    await page.keyboard.press("ControlOrMeta+Shift+p");
    await expect(box).toHaveClass(/\bopen\b/, { timeout: 1000 });
  }).toPass({ timeout: 10_000 });
  await input.fill(">copy");

  // No Terminal-category Copy row — the gate held.
  await expect(
    page
      .locator(".tb-omnibar-row", { hasText: "Copy" })
      .filter({ has: page.locator(".tb-row-dir", { hasText: "Terminal" }) }),
  ).toHaveCount(0);
});
