import type { Page } from "@playwright/test";
import { awaitEditorReady, openFile, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Find in Files journeys: seeding from the editor selection, arrow live-preview vs Enter commit (cursor on
// the match's column), the match-option toggles + include/exclude globs on their advertised chords, F4
// stepping from the editor, and the loud error strip for a bad regex. Real git grep over the seeded
// workspace — deterministic, no claude involvement.

// The live Monaco editor published for e2e (the full IStandaloneCodeEditor; declared structurally because
// e2e sits outside the app tsconfig).
interface EditorHandle {
  focus(): void;
  setSelection(range: {
    startLineNumber: number;
    startColumn: number;
    endLineNumber: number;
    endColumn: number;
  }): void;
  getPosition(): { lineNumber: number; column: number } | null;
}
type WeavieWindow = Window & { __WEAVIE_EDITOR__?: EditorHandle };

// Opens the search panel via its chord, retried like runCommand: a focused xterm/Monaco occasionally
// swallows the first chord under load.
async function openSearch(page: Page): Promise<void> {
  await expect(async () => {
    await page.keyboard.press("ControlOrMeta+Shift+f");
    await expect(page.locator(".search-panel")).toBeVisible({ timeout: 1000 });
  }).toPass({ timeout: 10_000 });
}

// The editor caret, read from the published handle.
async function caret(page: Page): Promise<{ lineNumber: number; column: number } | null> {
  return page.evaluate(() => (window as WeavieWindow).__WEAVIE_EDITOR__?.getPosition() ?? null);
}

async function typography(locator: import("@playwright/test").Locator): Promise<{
  family: string;
  size: number;
  weight: string;
}> {
  return locator.evaluate((element) => {
    const style = getComputedStyle(element);
    return {
      family: style.fontFamily,
      size: Number.parseFloat(style.fontSize),
      weight: style.fontWeight,
    };
  });
}

// Opens hello.ts and selects `greet` on line 1 ("export function greet" → columns 17-22) via the handle,
// so the selection is deterministic rather than driven by double-click hit-testing. openFile waits for the
// editor to actually bind the model (data-active-file) before the selection is applied.
async function selectGreet(page: Page): Promise<void> {
  await openFile(page, "hello.ts");
  await page.evaluate(() => {
    const editor = (window as WeavieWindow).__WEAVIE_EDITOR__;
    if (editor === undefined) {
      throw new Error("editor handle not available");
    }
    editor.focus();
    editor.setSelection({ startLineNumber: 1, startColumn: 17, endLineNumber: 1, endColumn: 22 });
  });
}

test("seeds from the selection, previews on arrows, and Enter lands on the match column", async ({
  page,
}) => {
  await selectGreet(page);
  await openSearch(page);

  // The query seeded from the selection and the search ran without any typing.
  const input = page.locator(".search-input");
  await expect(input).toHaveValue("greet");
  await expect(input).toBeFocused();
  const rows = page.locator(".search-row");
  await expect(rows).toHaveCount(2); // hello.ts lines 1 and 5
  await expect(page.locator(".search-summary")).toHaveText("2 matches in 1 file");
  // The matched substring is highlighted within the preview.
  await expect(rows.first().locator("mark.tb-hl").first()).toHaveText("greet");

  // ArrowDown moves the selection and live-previews: the caret lands on line 5's match column while focus
  // STAYS in the search input (the whole point of preview vs commit).
  await page.keyboard.press("ArrowDown");
  await expect(rows.nth(1)).toHaveClass(/\bselected\b/);
  await expect.poll(() => caret(page)).toEqual({ lineNumber: 5, column: 17 });
  await expect(input).toBeFocused();

  // Enter commits: same position, focus handed to the editor, panel still open for the next step.
  await page.keyboard.press("Enter");
  await expect(input).not.toBeFocused();
  await expect.poll(() => caret(page)).toEqual({ lineNumber: 5, column: 17 });
  await expect(page.locator(".search-panel")).toBeVisible();

  // F4 / Shift+F4 step the results from the editor without refocusing the panel (wraps clamp at the ends).
  await page.keyboard.press("Shift+F4");
  await expect.poll(() => caret(page)).toEqual({ lineNumber: 1, column: 17 });
  await expect(rows.nth(0)).toHaveClass(/\bselected\b/);

  // Esc from the panel closes it and returns focus to the editor.
  await openSearch(page); // refocus the input (no selection → the query is kept, just reselected)
  await expect(input).toHaveValue("greet");
  await page.keyboard.press("Escape");
  await expect(page.locator(".search-panel")).not.toBeVisible();
});

test("match-case / whole-word / regex chords and include-exclude globs shape the results", async ({
  page,
}) => {
  await awaitEditorReady(page);
  await openSearch(page);
  const input = page.locator(".search-input");
  const groups = page.locator(".search-group-name");

  // Case-insensitive by default: HELLO finds the seeded "Hello" texts in README.md and hello.ts.
  await input.fill("HELLO");
  await expect(groups.filter({ hasText: "README.md" })).toHaveCount(1);
  await expect(groups.filter({ hasText: "hello.ts" })).toHaveCount(1);

  // Alt+C (advertised on the toggle) flips Match Case: no uppercase HELLO exists.
  const caseToggle = page.locator(".search-toggle").nth(0);
  await expect(caseToggle).toHaveAttribute("title", /Match case \(Alt\+C\)/);
  await page.keyboard.press("Alt+c");
  await expect(caseToggle).toHaveAttribute("aria-pressed", "true");
  await expect(page.locator(".search-empty")).toContainText("No results");
  await page.keyboard.press("Alt+c");
  await expect(groups.filter({ hasText: "hello.ts" })).toHaveCount(1);

  // Whole word: "gree" is only a fragment of "greet".
  await input.fill("gree");
  await expect(page.locator(".search-row").first()).toBeVisible();
  await page.keyboard.press("Alt+w");
  await expect(page.locator(".search-toggle").nth(1)).toHaveAttribute("aria-pressed", "true");
  await expect(page.locator(".search-empty")).toContainText("No results");
  await page.keyboard.press("Alt+w");

  // Regex: "gre.t" matches nothing literally, but as a pattern it finds greet.
  await input.fill("gre.t");
  await expect(page.locator(".search-empty")).toContainText("No results");
  await page.keyboard.press("Alt+r");
  await expect(page.locator(".search-toggle").nth(2)).toHaveAttribute("aria-pressed", "true");
  await expect(groups.filter({ hasText: "hello.ts" })).toHaveCount(1);

  // A regex git can't parse fails LOUDLY in the error strip — never reported as "No results".
  await input.fill("[");
  await expect(page.locator(".search-error")).toContainText("Search failed");
  await page.keyboard.press("Alt+r");

  // Include/exclude globs (always visible — no toggle): include *.ts drops README.md; excluding hello.ts
  // then empties it, since the include already narrowed to that one file.
  await input.fill("Hello");
  await expect(groups.filter({ hasText: "README.md" })).toHaveCount(1);
  const include = page.locator(".search-glob").nth(0);
  const exclude = page.locator(".search-glob").nth(1);
  await expect(include).toBeVisible();
  await include.fill("*.ts");
  await expect(groups.filter({ hasText: "README.md" })).toHaveCount(0);
  await expect(groups.filter({ hasText: "hello.ts" })).toHaveCount(1);
  await exclude.fill("hello.ts");
  await expect(page.locator(".search-empty")).toContainText("check the include/exclude filters");
});

test("code results follow editor typography while search chrome stays compact", async ({
  page,
}) => {
  await openFile(page, "hello.ts");
  await openSearch(page);
  await page.locator(".search-input").fill("greet");

  const editorLine = page.locator(".monaco-editor .view-line").first();
  const preview = page.locator(".search-row-preview").first();
  const metadata = page.locator(".search-group-name").first();
  const input = page.locator(".search-input");
  const hint = page.locator(".search-summary");
  await expect(preview).toBeVisible();

  const initialEditor = await typography(editorLine);
  const initialResult = await typography(preview);
  const publishedFamily = await page.evaluate(() =>
    document.documentElement.style.getPropertyValue("--editor-font-family"),
  );
  expect(initialResult.family).toBe(publishedFamily);
  expect(initialEditor.family.startsWith(publishedFamily)).toBe(true);
  expect(initialResult.size).toBe(initialEditor.size);
  expect(initialResult.weight).toBe(initialEditor.weight);
  expect((await typography(metadata)).size).toBeCloseTo(initialEditor.size * 0.8125, 4);
  expect((await typography(input)).size).toBe(12);
  expect((await typography(hint)).size).toBe(11);

  await page.keyboard.press("ControlOrMeta+=");
  await expect.poll(async () => (await typography(preview)).size).toBe(initialEditor.size + 1);
  await expect
    .poll(async () => (await typography(metadata)).size)
    .toBeCloseTo((initialEditor.size + 1) * 0.8125, 4);
  expect((await typography(input)).size).toBe(12);

  await page.keyboard.press("ControlOrMeta+0");
});

test("a session switch clears stale results so stepping can't open the previous worktree", async ({
  page,
}) => {
  await awaitEditorReady(page);
  await openSearch(page);
  await page.locator(".search-input").fill("greet");
  await expect(page.locator(".search-row").first()).toBeVisible();

  // Forking switches to a new session on its own worktree; the prior worktree's results (and any pending
  // F4 target) must not survive, or stepping would open a path that routes into the wrong worktree.
  await runCommand(page, "Fork Session");
  await expect(page.locator(".session-chip")).toHaveCount(2);
  await expect(page.locator(".search-row")).toHaveCount(0);
  // F4 with the cleared list is a no-op — no tab opens.
  await page.keyboard.press("F4");
  await expect(page.locator(".search-row")).toHaveCount(0);
});
