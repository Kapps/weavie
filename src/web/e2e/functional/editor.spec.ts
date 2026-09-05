import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { clickIntoEditor, openFile, typeInEditor } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import type { WeavieWindow } from "../harness/weavie-window";

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

// Failed on main 2026-09-04 11:47 UTC on macos shard 3/6 (`Expected: > 1, Received: 1`):
// https://github.com/Kapps/weavie/actions/runs/33869160511/job/101011489774. Root cause: the curated
// grammar's TextMate tokenization is applied asynchronously after the language id is set, so reading
// `tokenClasses` once right after the language-id poll could catch the model before its first
// tokenization pass landed. `awaitEditorLaidOut` (via `openFile`) only proves Monaco painted lines, not
// that tokenization finished. Fixed by polling `tokenClasses` itself instead of reading it once.
test("curated Python and Rust keep their language ids and shared-scope highlighting", async ({
  page,
  weavie,
}) => {
  const files = [
    ["sample.py", "python", "def greet(name: str) -> str:\n    return f'Hello {name}'\n"],
    ["sample.rs", "rust", 'pub fn greet(name: &str) -> String { format!("Hello {name}") }\n'],
    ["sample.bzl", "python", 'def greet(name):\n    return "Hello %s" % name\n'],
  ] as const;

  for (const [name, languageId, source] of files) {
    await writeFile(join(weavie.workspace, name), source);
    await openFile(page, name);
    await expect
      .poll(() =>
        page.evaluate(() =>
          (window as WeavieWindow).__WEAVIE_EDITOR__?.getModel()?.getLanguageId(),
        ),
      )
      .toBe(languageId);
    await expect
      .poll(() =>
        page
          .locator(".monaco-editor .view-lines [class*='mtk']")
          .evaluateAll((elements) => new Set(elements.map((element) => element.className)).size),
      )
      .toBeGreaterThan(1);
  }
});

// Clicking into the editor must not scroll the file out of view. Monaco sizes `.view-lines` to the whole
// scrollable content, so with `scrollBeyondLastLine` it is taller than the viewport even for a 7-line file —
// and Playwright reveals an element before clicking it. The browser satisfies that reveal by natively
// scrolling the `overflow:hidden` guard, which Monaco folds straight back into its own scroll position
// (`editorScrollbar.ts`'s `onBrowserDesperateReveal`), leaving the editor pinned at its last line with every
// other line absent from the DOM. That cost this suite six red CI runs before the cause was found, so both
// halves are pinned here: the damage the container does, and that the helper's target cannot do it.
test("clicking into the editor never scrolls the file away", async ({ page }) => {
  await openFile(page, "hello.ts");

  const sizes = await page.evaluate(() => {
    const box = (selector: string) =>
      (document.querySelector(`.monaco-editor ${selector}`) as HTMLElement).getBoundingClientRect()
        .height;
    return {
      guard: box(".overflow-guard"),
      container: box(".view-lines"),
      line: box(".view-line"),
    };
  });
  // The container overflows the viewport — revealing it has somewhere to scroll to...
  expect(sizes.container).toBeGreaterThan(sizes.guard);
  // ...and a single line does not, which is why that is what `clickIntoEditor` targets.
  expect(sizes.line).toBeLessThanOrEqual(sizes.guard);

  // And a native scroll of the guard — what the browser does to reveal an element taller than it — is folded
  // into Monaco's own scroll position, landing on a legal maximum that nothing scrolls back from. One line
  // left in the DOM is the fingerprint this cost six CI runs to recognise.
  const scrolled = await page.evaluate(async () => {
    const guard = document.querySelector(".monaco-editor .overflow-guard") as HTMLElement;
    guard.scrollTop = 500;
    await new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    return {
      guardScrollTop: guard.scrollTop,
      renderedLines: document.querySelectorAll(".view-line").length,
    };
  });
  expect(scrolled).toEqual({ guardScrollTop: 0, renderedLines: 1 });

  // The helper leaves the whole file on screen.
  await page.reload();
  await openFile(page, "hello.ts");
  await clickIntoEditor(page);
  await expect(page.locator(".monaco-editor .view-line")).toHaveCount(7);
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
  await clickIntoEditor(page);
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
  await clickIntoEditor(page);
  await page.keyboard.press("ControlOrMeta+Home");
  await typeInEditor(page, marker);

  const tab = page.locator(".editor-tab", { hasText: "hello.ts" });
  await expect(tab.locator(".editor-tab-dirty")).toBeVisible();

  await page.keyboard.press("ControlOrMeta+s");
  await expect(tab.locator(".editor-tab-dirty")).toHaveCount(0);

  const onDisk = await readFile(join(weavie.workspace, "hello.ts"), "utf8");
  expect(onDisk).toContain(marker.trim());
});

// Workspace invalidations are file-provider concerns, not language-server concerns: an external edit to a
// non-LSP file must still refresh the open Monaco model through the owning session's file provider.
test("an external Markdown edit refreshes the open editor model", async ({ page, weavie }) => {
  await openFile(page, "README.md");

  const marker = `external-markdown-${Date.now()}`;
  const path = join(weavie.workspace, "README.md");
  const before = await readFile(path, "utf8");
  await writeFile(path, `${before}\n${marker}\n`);

  const modelText = () =>
    page.evaluate(
      () =>
        (
          window as Window & { __WEAVIE_EDITOR__?: { getModel(): { getValue(): string } | null } }
        ).__WEAVIE_EDITOR__
          ?.getModel()
          ?.getValue() ?? null,
    );
  await expect.poll(modelText).toContain(marker);
});
