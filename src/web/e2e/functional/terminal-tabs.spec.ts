import { createSession } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test("shell terminal tabs create, cycle, close, and recover from empty", async ({ page }) => {
  const shell = page.locator('.terminal-surface[data-kind="terminal:shell"]');
  const tabs = shell.locator(".shell-tab");
  const activeTab = shell.locator(".shell-tab.active");
  const newTerminal = shell.getByRole("button", { name: "New terminal" });

  await expect(tabs).toHaveCount(1);
  await shell.locator(".shell-tab-main").click();
  await page.keyboard.press("Control+Shift+T");
  await expect(tabs).toHaveCount(2);
  await expect(tabs.nth(1)).toHaveClass(/\bactive\b/);

  await page.keyboard.press("Control+Tab");
  await expect(tabs.first()).toHaveClass(/\bactive\b/);
  await page.keyboard.press("Control+Shift+Tab");
  await expect(tabs.nth(1)).toHaveClass(/\bactive\b/);

  await page.keyboard.press("Control+Shift+W");
  await expect(tabs).toHaveCount(1);
  await expect(activeTab).toHaveCount(1);
  await page.keyboard.press("Control+Shift+W");
  await expect(tabs).toHaveCount(0);
  await expect(newTerminal).toBeFocused();

  await page.keyboard.press("Control+Shift+W");
  await expect(page.locator(".toast", { hasText: "No shell terminal is open." })).toBeVisible();
  await expect(newTerminal).toBeFocused();

  await page.keyboard.press("Control+Shift+T");
  await expect(tabs).toHaveCount(1);
  await expect(activeTab).toHaveCount(1);
});

test("an empty persisted shell set boots with a structured agent", async ({ page }) => {
  await createSession(page, { branch: "e2e/empty-shell", provider: "fake-acp" });
  const shell = page.locator('.terminal-surface[data-kind="terminal:shell"]');
  const tabs = shell.locator(".shell-tab");
  await expect(tabs).toHaveCount(1);
  await shell.locator(".shell-tab-main").click();
  await page.keyboard.press("Control+Shift+W");
  await expect(tabs).toHaveCount(0);

  await page.reload({ waitUntil: "domcontentloaded" });

  await expect(page.locator("#splash")).toHaveCount(0, { timeout: 40_000 });
  await expect(page.locator('[data-surface="structured-agent"]')).toBeVisible();
  await expect(tabs).toHaveCount(0);
});
