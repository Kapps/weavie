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

  const initial = 16; // the default font.size
  // Poll: Monaco can re-render the line between visibility and the style read, detaching the sampled
  // element mid-read (getComputedStyle on a detached node yields "" → NaN).
  await expect.poll(() => editorFontSize(page)).toBe(initial);

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

// Regression: a normal font zoom is a *silent* Core success, which round-trips as `message: null` (not
// `undefined`). The feedback guard once compared `message !== undefined`, so each zoom fired `notify("info",
// null)` — an empty "broken" toast (a box with only the ✕ close button), stacking one per keypress. Pin that
// zooming via the real keybindings never spawns a message-less toast.
test("zooming via the keybindings does not spawn empty toasts", async ({ page }) => {
  await openFile(page, "hello.ts");
  const line = page.locator(".monaco-editor .view-line").first();
  await expect(line).toBeVisible();
  await line.click(); // focus the editor — the bug fired on the chord with the editor focused

  for (let i = 0; i < 6; i++) {
    await page.keyboard.press("ControlOrMeta+="); // Increase Font Size
  }
  await page.keyboard.press("ControlOrMeta+-"); // Decrease Font Size
  await page.keyboard.press("ControlOrMeta+0"); // Reset Font Size (silent success)

  // After the dust settles, no toast on screen may be empty: an empty `.toast-msg` (the broken box with only
  // the ✕) is the regression. Reads every toast's message text directly rather than relying on `hasText`,
  // which can't distinguish a message-less node from an absent one.
  await expect(async () => {
    const messages = await page.locator(".toast .toast-msg").allTextContents();
    expect(messages.filter((m) => m.trim() === "")).toEqual([]);
  }).toPass({ timeout: 3000 });
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
