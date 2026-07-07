import { openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Which pane currently holds DOM focus, as the data-kind of the focused element's surface. This is the ground
// truth for "where will my keystrokes go" — independent of the visual `.active` highlight.
const focusedKind = (page: import("@playwright/test").Page): Promise<string | null> =>
  page.evaluate(
    () => document.activeElement?.closest("[data-kind]")?.getAttribute("data-kind") ?? null,
  );

// Regression: clicking a pane's chrome must move focus into that pane. The terminal head holds no focusable
// element, so before the fix the click was a no-op — DOM focus stayed in the editor while the user believed
// the terminal was selected, and their next keystroke went to Monaco. Pure frontend focus routing → headless.
test("clicking the terminal head focuses the terminal, not the editor", async ({ page }) => {
  const editor = page.locator('.editor-surface[data-kind="editor"]');
  const shell = page.locator('.terminal-surface[data-kind="terminal:shell"]');

  // Start with the editor genuinely focused: open a file and click into Monaco.
  await openFile(page, "hello.ts");
  await page.locator(".monaco-editor .view-lines").first().click();
  await expect(editor).toHaveClass(/\bactive\b/);

  // Click the terminal's head — the title bar, the natural "select this terminal" target.
  await shell.locator(".pane-head").click();

  // Focus moved: the terminal is highlighted, the editor isn't, and DOM focus is inside the terminal.
  await expect(shell).toHaveClass(/\bactive\b/);
  await expect(editor).not.toHaveClass(/\bactive\b/);
  expect(await focusedKind(page)).toBe("terminal:shell");
});

// The behavioural half of the same bug: after selecting the terminal by its head, keystrokes must reach the
// PTY and leave the editor untouched. Before the fix they were inserted into Monaco.
test("typing after clicking the terminal head does not leak into the editor", async ({ page }) => {
  const viewLines = page.locator(".monaco-editor .view-lines").first();

  await openFile(page, "hello.ts");
  await viewLines.click();
  await page.keyboard.press("ControlOrMeta+Home");

  await page.locator('.terminal-surface[data-kind="terminal:shell"] .pane-head').click();
  await page.keyboard.type("focusXYZZY");

  // The editor never received the keystrokes — the marker is absent and the seeded source is intact.
  await expect(viewLines).not.toContainText("focusXYZZY");
  await expect(viewLines).toContainText("greet");
});

// Two stacked terminals: clicking the inactive one's head switches focus between them. Same root cause — the
// head was an inert click target — and the main way a user moves between the claude and shell panes by mouse.
test("clicking a second terminal's head switches focus between terminals", async ({ page }) => {
  const claude = page.locator('.terminal-surface[data-kind="agent"]');
  const shell = page.locator('.terminal-surface[data-kind="terminal:shell"]');

  await shell.locator(".pane-head").click();
  await expect(shell).toHaveClass(/\bactive\b/);
  expect(await focusedKind(page)).toBe("terminal:shell");

  await claude.locator(".pane-head").click();
  await expect(claude).toHaveClass(/\bactive\b/);
  await expect(shell).not.toHaveClass(/\bactive\b/);
  expect(await focusedKind(page)).toBe("agent");
});
