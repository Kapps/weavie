import { clickIntoEditor, openCommandPalette } from "../harness/actions";
import { withHeldAnimationFrames } from "../harness/animation-frames";
import { expect, test } from "../harness/fixtures";

// Regression (#108): opening the command palette moves DOM focus to the omnibar input, which fired focusin
// and reset the focus-context (editorFocused/terminalFocused → false). The palette filtered commands by
// their `when` BEFORE accounting for that, so every focus-gated command was dropped — searching ">copy"
// with a terminal focused returned ZERO rows. The fix evaluates `when` against the pane focused when the
// palette opened. Pure frontend `when`-evaluation against pre-open focus → headless.
test("palette tracks terminal and editor focus when gating Copy and browser Paste", async ({
  page,
}) => {
  const shell = page.locator('.terminal-surface[data-kind="terminal:shell"]');
  const input = page.locator(".tb-omnibar-input");

  // Focus a terminal pane — clicking its tab lands DOM focus on the pane's xterm (terminalFocused = true).
  await shell.locator(".shell-tab-main").click();
  await expect(shell).toHaveClass(/\bactive\b/);

  // Open the palette (omnibar command mode); focus now sits in the omnibar input, not the terminal.
  await openCommandPalette(page);
  await input.fill(">copy");

  // The terminal-gated Copy command (When="terminalFocused") is visible — proven by both its row and the
  // Terminal category it carries (the file-search "No matching files" path can't produce these).
  const copyRow = page.locator(".tb-omnibar-row", { hasText: "Copy" });
  await expect(copyRow.first()).toBeVisible();
  await expect(
    copyRow.filter({ has: page.locator(".tb-row-dir", { hasText: "Terminal" }) }),
  ).toHaveCount(1);

  // Paste carries the same terminalFocused gate AND `!browserShell`: a browser tab (this headless harness)
  // can't read the clipboard programmatically, so the palette must NOT offer a Paste that could only no-op —
  // there Ctrl+V falls through to xterm's native paste. So with a terminal focused, the Terminal Paste row is absent.
  await input.fill(">paste");
  await expect(page.locator(".tb-omnibar-box")).toHaveClass(/\bopen\b/);
  await expect(
    page
      .locator(".tb-omnibar-row", { hasText: "Paste" })
      .filter({ has: page.locator(".tb-row-dir", { hasText: "Terminal" }) }),
  ).toHaveCount(0);

  await page.keyboard.press("Escape");
  await test.step("editor focus hides terminal Copy", async () => {
    // Open a file and click into Monaco so editorFocused (not terminalFocused) is the pre-open focus.
    await input.click();
    await input.fill("hello.ts");
    await expect(page.locator(".tb-omnibar-row", { hasText: "hello.ts" }).first()).toBeVisible();
    await input.press("Enter");
    await expect(page.locator(".editor-tab", { hasText: "hello.ts" })).toBeVisible();
    await clickIntoEditor(page);
    await expect(page.locator('.editor-surface[data-kind="editor"]')).toHaveClass(/\bactive\b/);

    await openCommandPalette(page);
    await input.fill(">copy");

    // No Terminal-category Copy row — the gate held.
    await expect(
      page
        .locator(".tb-omnibar-row", { hasText: "Copy" })
        .filter({ has: page.locator(".tb-row-dir", { hasText: "Terminal" }) }),
    ).toHaveCount(0);
  });
});

// Hold frames across a shell-tab selection and a newer keyboard command.
test("a deferred terminal focus cannot close the command palette", async ({ page }) => {
  const tab = page.locator('.terminal-surface[data-kind="terminal:shell"] .shell-tab-main');
  const bounds = await tab.boundingBox();
  expect(bounds).not.toBeNull();
  await withHeldAnimationFrames(page, async (release) => {
    await page.mouse.click(bounds!.x + bounds!.width / 2, bounds!.y + bounds!.height / 2);
    const terminalInput = page.locator(
      '.terminal-surface[data-kind="terminal:shell"] .xterm-helper-textarea',
    );
    await expect(terminalInput).toBeFocused();
    await page.keyboard.press("x");
    await expect(terminalInput).toBeFocused();
    await page.keyboard.press("ControlOrMeta+Shift+p");
    const input = page.locator(".tb-omnibar-input");
    await expect(page.locator(".tb-omnibar-box")).toHaveClass(/\bopen\b/);
    await expect(input).toBeFocused();
    await release();
    await expect(page.locator(".tb-omnibar-box")).toHaveClass(/\bopen\b/);
    await expect(input).toBeFocused();
  });
});
