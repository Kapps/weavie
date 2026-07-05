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

// Highlighting must survive an EDIT, not just first render. monaco-vscode-api's incremental re-tokenizer loads
// vscode-textmate's diff helpers (applyStateStackDiff / diffStateStacksRefEq / INITIAL) through a dynamic import
// a bundler can flatten to `undefined` — freshly typed lines then never colour (a silent, edit-only break). This
// guards vite.config.ts's `fixTextmateLazyImport` workaround across bundler swaps (Rollup ↔ Rolldown). Pure
// frontend, so headless-only.
test("syntax highlighting survives typing new code (incremental re-tokenization)", async ({
  page,
}) => {
  await openFile(page, "hello.ts");

  // Type a distinctive line AFTER first render, so its tokens come purely from the incremental re-tokenizer.
  await page.locator(".monaco-editor .view-lines").first().click();
  await page.keyboard.press("ControlOrMeta+End");
  await page.keyboard.type("\nconst added: number = 987654;");

  // Once the async worker re-tokenizes, the new line carries several distinct token classes (const / number
  // type / numeric literal / identifier). A broken incremental tokenizer leaves it a single flat default run.
  const typedLineClasses = () =>
    page.locator(".monaco-editor .view-line").evaluateAll((lines) => {
      const line = lines.find((l) => (l.textContent ?? "").includes("987654"));
      return line
        ? new Set(
            Array.from(line.querySelectorAll("[class*='mtk']"))
              .flatMap((s) => s.className.split(/\s+/))
              .filter((c) => /^mtk\d+$/.test(c)),
          ).size
        : 0;
    });
  await expect.poll(typedLineClasses, { timeout: 10_000 }).toBeGreaterThan(2);
});

// Edit a file → the tab goes dirty → save → the dirty marker clears AND the new content is on disk. The
// clean signal is the dirty marker disappearing (the fs-write round-trip completed), never a fixed sleep.
// Persistence is the host-side seam, so this also runs on remote (where the write lands on the worker).
test("editing then saving persists to disk @cross", async ({ page, weavie }) => {
  // @cross: on the remote worker hop under a loaded CI box the editor cold-boot alone can eat most of the 30s
  // default before the edit/save round-trip even starts. Give it the room (test.slow triples the budget); this
  // marks the test slow, it does not retry it.
  test.slow();
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
