import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { openFile, typeInEditor } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Omnibar → open a file → Monaco renders it with syntax highlighting. Highlighting is observed via Monaco's
// tokenization classes (`.mtk<n>` spans) in the rendered view lines — proof tokens were produced, not just
// plain text. Pure frontend, so headless-only.
test("omnibar opens a file and Monaco highlights it", async ({ page }) => {
  await openFile(page, "hello.ts");

  const viewLines = page.locator(".monaco-editor .view-lines");
  await expect(viewLines).toContainText("greet");
  const tokenClasses = await page
    .locator(".monaco-editor .view-lines [class*='mtk']")
    .evaluateAll((els) => Array.from(new Set(els.map((el) => el.className))));
  expect(tokenClasses.length).toBeGreaterThan(1);
});

// Edit a file → the tab goes dirty → save → the dirty marker clears AND the new content is on disk. The
// clean signal is the dirty marker disappearing (the fs-write round-trip completed), never a fixed sleep.
// Persistence is the host-side seam, so this also runs on remote (where the write lands on the worker).
test("editing then saving persists to disk @cross", async ({ page, weavie }) => {
  await openFile(page, "hello.ts");

  const marker = `// edit-${Date.now()}\n`;
  await page.locator(".monaco-editor .view-lines").first().click();
  await page.keyboard.press("ControlOrMeta+Home");
  await typeInEditor(page, marker);

  const tab = page.locator(".editor-tab", { hasText: "hello.ts" });
  await expect(tab.locator(".editor-tab-dirty")).toBeVisible();

  await page.keyboard.press("ControlOrMeta+s");
  await expect(tab.locator(".editor-tab-dirty")).toHaveCount(0);

  const onDisk = await readFile(join(weavie.workspace, "hello.ts"), "utf8");
  expect(onDisk).toContain(marker.trim());
});
