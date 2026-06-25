import { openFile, runCommand, typeInEditor } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Text-editing journeys beyond the open→save→persist path in editor.spec.ts: Monaco-native undo/redo isn't
// hijacked by Weavie's diff keybindings, per-tab buffers stay isolated across a tab switch, and an unsaved
// scratch buffer guards its close. Pure frontend (Monaco + tab store), so headless-only.

test("undo and redo work in the editor and aren't hijacked by diff keybindings", async ({
  page,
}) => {
  await openFile(page, "hello.ts");
  const viewLines = page.locator(".monaco-editor .view-lines");

  await viewLines.first().click();
  await page.keyboard.press("ControlOrMeta+Home");
  // A contiguous token is a single Monaco undo unit, so one undo removes exactly it.
  await page.keyboard.type("UNDOMARKER");
  await expect(viewLines).toContainText("UNDOMARKER");

  await page.keyboard.press("ControlOrMeta+z");
  await expect(viewLines).not.toContainText("UNDOMARKER");

  await page.keyboard.press("ControlOrMeta+y");
  await expect(viewLines).toContainText("UNDOMARKER");
});

test("edits in two tabs stay isolated across a tab switch", async ({ page }) => {
  await openFile(page, "hello.ts");
  await page.locator(".monaco-editor .view-lines").first().click();
  await page.keyboard.press("ControlOrMeta+Home");
  await typeInEditor(page, "HELLOEDIT");

  await openFile(page, "notes.txt");
  await page.locator(".monaco-editor .view-lines").first().click();
  await page.keyboard.press("ControlOrMeta+Home");
  await typeInEditor(page, "NOTESEDIT");
  await expect(page.locator(".monaco-editor .view-lines")).toContainText("NOTESEDIT");

  // Back to hello.ts: its buffer kept its own edit and never picked up notes.txt's.
  await page.locator(".editor-tab", { hasText: "hello.ts" }).click();
  const viewLines = page.locator(".monaco-editor .view-lines");
  await expect(viewLines).toContainText("HELLOEDIT");
  await expect(viewLines).not.toContainText("NOTESEDIT");
});

test("closing an unsaved scratch buffer prompts before discarding", async ({ page }) => {
  await runCommand(page, "New File");
  const tab = page.locator(".editor-tab").last();
  await expect(tab).toBeVisible();

  await page.locator(".monaco-editor .view-lines").first().click();
  await typeInEditor(page, "SCRATCHEDIT");

  await tab.hover();
  await tab.locator(".editor-tab-close").click();

  // A dirty scratch (no autosave target) guards its close; confirming discards it.
  const dialog = page.locator(".confirm-dialog");
  await expect(dialog).toBeVisible();
  await dialog.locator(".confirm-btn-primary").click();
  await expect(page.locator(".editor-tab")).toHaveCount(0);
});
