import { type Page, expect } from "@playwright/test";
import { mediaTypeOf } from "../../src/editor/media/media-types";

// Open a workspace file through the omnibar's "Go to File" and wait until the editor is ACTUALLY showing it.
// The first fuzzy match is auto-selected, so typing the name and pressing Enter opens it.
export async function openFile(page: Page, name: string): Promise<void> {
  await page.locator(".tb-omnibar-input").click();
  await page.locator(".tb-omnibar-input").fill(name);
  await expect(page.locator(".tb-omnibar-row", { hasText: name }).first()).toBeVisible();
  await page.locator(".tb-omnibar-input").press("Enter");
  await expect(page.locator(".editor-tab", { hasText: name })).toBeVisible();
  // The tab appears (and its active state + the current-file flip) BEFORE the Monaco model swap lands — that
  // swap is an async host round-trip. Typing in the gap leaks into the outgoing model, so wait for the editor
  // to actually bind this file (data-active-file, stamped on the real swap). Media files never bind a model.
  if (mediaTypeOf(name) === null) {
    const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"); // every regex metachar, backslash included
    await expect(page.locator(".editor")).toHaveAttribute(
      "data-active-file",
      new RegExp(`[\\\\/]${escaped}$`),
    );
  }
}

// Run a command through the command palette (Show All Commands), matching by title text. Exercises the
// same keyboard path a user would: $mod+Shift+p, type, Enter on the first match.
export async function runCommand(page: Page, title: string): Promise<void> {
  const box = page.locator(".tb-omnibar-box");
  // Ensure the palette is closed first, so the open shortcut doesn't toggle a still-open palette shut
  // (it stays open briefly after a prior command runs).
  await page.keyboard.press("Escape");
  await expect(box).not.toHaveClass(/\bopen\b/);
  // Open it — retried because a focused pane (xterm/Monaco) occasionally swallows the first chord under
  // load, so the keypress doesn't reach the global handler.
  await expect(async () => {
    await page.keyboard.press("ControlOrMeta+Shift+p");
    await expect(box).toHaveClass(/\bopen\b/, { timeout: 1000 });
  }).toPass({ timeout: 10_000 });
  // Command mode is signalled by a leading ">"; keep it on the filled value (a bare fill would drop to
  // file search).
  await page.locator(".tb-omnibar-input").fill(`>${title}`);
  await expect(page.locator(".tb-omnibar-row", { hasText: title }).first()).toBeVisible();
  await page.locator(".tb-omnibar-input").press("Enter");
  await expect(box).not.toHaveClass(/\bopen\b/);
}

// Type text at the current caret in the focused Monaco editor.
export async function typeInEditor(page: Page, text: string): Promise<void> {
  await page.locator(".monaco-editor .view-lines").first().click();
  await page.keyboard.type(text);
}
