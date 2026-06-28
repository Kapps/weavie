import { openFile, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Reads the rendered editor font size: Monaco stamps `font-size` onto each `.view-line`, so the computed
// value is the real on-screen pixel size, not just a setting.
async function editorFontSize(page: import("@playwright/test").Page): Promise<number> {
  return page
    .locator(".monaco-editor .view-line")
    .first()
    .evaluate((el) => Number.parseFloat(getComputedStyle(el).fontSize));
}

// Issue #120: there was no command or keybinding to change the font size — only hand-editing settings. The
// new View commands adjust the global font.size setting, which the web applies live to the editor. This pins
// that the rendered editor font actually grows/shrinks/resets when the commands run. Pure frontend +
// Core-setting round-trip through the headless host, so headless-only.
test("Increase / Decrease / Reset Font Size resize the editor live", async ({ page }) => {
  await openFile(page, "hello.ts");
  await expect(page.locator(".monaco-editor .view-line").first()).toBeVisible();

  const initial = await editorFontSize(page);
  expect(initial).toBe(16); // the default font.size

  // Increase three times: the rendered editor font must grow.
  for (let i = 0; i < 3; i++) {
    await runCommand(page, "Increase Font Size");
  }
  await expect(async () => {
    expect(await editorFontSize(page)).toBe(initial + 3);
  }).toPass({ timeout: 5000 });

  // Decrease once: it shrinks back by a pixel.
  await runCommand(page, "Decrease Font Size");
  await expect(async () => {
    expect(await editorFontSize(page)).toBe(initial + 2);
  }).toPass({ timeout: 5000 });

  // Reset returns to the default.
  await runCommand(page, "Reset Font Size");
  await expect(async () => {
    expect(await editorFontSize(page)).toBe(initial);
  }).toPass({ timeout: 5000 });
});

// The commands must be discoverable in the palette under the View category — that's the keyboard-first
// discovery path the issue asked for.
test("font-size commands appear in the palette under View", async ({ page }) => {
  const box = page.locator(".tb-omnibar-box");
  await expect(async () => {
    await page.keyboard.press("ControlOrMeta+Shift+p");
    await expect(box).toHaveClass(/\bopen\b/, { timeout: 1000 });
  }).toPass({ timeout: 10_000 });
  await page.locator(".tb-omnibar-input").fill(">font size");

  for (const title of ["Increase Font Size", "Decrease Font Size", "Reset Font Size"]) {
    const row = page.locator(".tb-omnibar-row", { hasText: title }).first();
    await expect(row).toBeVisible();
    await expect(row).toContainText("View");
  }
});
