import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Locator, Page } from "@playwright/test";
import { awaitEditorReady, awaitFontsSettled, createSession, openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

// Find in Files journeys: seeding from the highlighted text (editor or agent transcript), arrow live-preview
// vs Enter commit (cursor on the match's column), the match-option toggles + include/exclude globs on their
// advertised chords, F4 stepping from the editor, and the loud error strip for a bad regex. Real git grep
// over the seeded workspace — deterministic, no claude involvement.

import type { WeavieWindow } from "../harness/weavie-window";

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

// Highlights a word in the transcript by dragging across it, the gesture a user makes — Playwright's
// synthetic double-click doesn't word-select in headless Chromium. The drag starts a pixel before the word
// and ends past the end of its line, so both ends land on a character boundary rather than mid-glyph.
async function dragOverWord(page: Page, within: Locator, word: string): Promise<void> {
  const rect = await within.evaluate((element, text) => {
    const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
      const start = (node as Text).data.indexOf(text);
      if (start >= 0) {
        const range = document.createRange();
        range.setStart(node, start);
        range.setEnd(node, start + text.length);
        const { x, y, width, height } = range.getBoundingClientRect();
        return { x, y, width, height };
      }
    }
    throw new Error(`no text node containing ${text}`);
  }, word);
  const middle = rect.y + rect.height / 2;
  await page.mouse.move(rect.x - 1, middle);
  await page.mouse.down();
  await page.mouse.move(rect.x + rect.width + 40, middle, { steps: 8 });
  await page.mouse.up();
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

test("a changed query immediately hides and disables results from the previous search", async ({
  page,
}) => {
  await openFile(page, "README.md");
  await openSearch(page);
  const input = page.locator(".search-input");
  await input.fill("greet");
  await expect(page.locator(".search-row")).toHaveCount(2);

  await input.fill("no-match-for-this-query");
  await expect(page.locator(".search-body")).toHaveAttribute("aria-busy", "true");
  await expect(page.getByText("Searching…", { exact: true })).toBeVisible();
  await expect(page.locator(".search-row")).toHaveCount(0);

  await page.keyboard.press("Enter");
  await page.keyboard.press("F4");
  await expect(page.locator(".editor")).toHaveAttribute("data-active-file", /README\.md$/);
  await expect(page.locator(".search-empty")).toHaveText("No results");
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
  // 2026-07-25: flaked on CI run https://github.com/Kapps/weavie/actions/runs/30143175785 —
  // initialEditor.family.startsWith(publishedFamily) was false. Root cause: the CSS var is published
  // synchronously but Monaco's own remeasure against the loaded webfont is scheduled, so the rendered
  // `.view-line` can still be measuring against a fallback font at this point. Fixed by waiting for
  // fonts to settle before reading computed typography (same pattern as editor-cursor.spec.ts's settle()).
  await awaitFontsSettled(page);

  // Flaked (Linux CI only) 2026-07-25 04:11 UTC:
  // https://github.com/Kapps/weavie/actions/runs/30143175785/job/89640122643 — initialEditor.family didn't
  // start with publishedFamily. Root cause: monaco-setup.ts creates the editor with the Go Mono stack before
  // the webfont has loaded, then only remeasures (monaco.editor.remeasureFonts()) once `document.fonts.ready`
  // resolves; reading .view-line's computed style before that could still observe Monaco's pre-remeasure
  // fallback metrics. Wait on the same signal the app remeasures on, so the read always lands after it.
  await page.evaluate(() => document.fonts.ready);

  const publishedFamily = await page.evaluate(() =>
    document.documentElement.style.getPropertyValue("--editor-font-family"),
  );
  // Monaco applies its font asynchronously (a layout pass after the CSS var lands), so read it once stable
  // rather than racing it — flaked on Linux CI 2026-07-25 04:11 UTC
  // (https://github.com/Kapps/weavie/actions/runs/30143175785/job/89640122643).
  await expect
    .poll(async () => (await typography(editorLine)).family.startsWith(publishedFamily))
    .toBe(true);
  // The search-row preview applies the same published font asynchronously too (its own layout pass, not
  // Monaco's), so it needs the same settle-before-read treatment as editorLine above — flaked on the
  // Windows shard 2026-08-13: https://github.com/Kapps/weavie/actions/runs/31664855484/job/94337743604
  // (read "Chivo, system-ui, sans-serif", the chrome fallback stack, instead of the published content font).
  await expect
    .poll(async () => (await typography(preview)).family.startsWith(publishedFamily))
    .toBe(true);
  const initialEditor = await typography(editorLine);
  const initialResult = await typography(preview);
  expect(initialResult.family).toBe(publishedFamily);
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

test("a session switch applies the visible query to the destination worktree", async ({
  page,
  weavie,
}) => {
  await awaitEditorReady(page);
  await createSession(page, { branch: "e2e/find-session-switch", provider: "claude" });
  const [forkedWorktree] = sessionWorktrees(weavie.workspace);
  if (forkedWorktree === undefined) {
    throw new Error("forked session did not create a git worktree");
  }
  const token = "SESSION_SEARCH_CANARY";
  await Promise.all([
    writeFile(join(weavie.workspace, "alpha-search.txt"), `${token}\n`),
    writeFile(join(forkedWorktree, "beta-search.txt"), `${token}\n`),
  ]);

  const chips = page.locator(".session-chip");
  await expect(chips).toHaveCount(2);
  await chips.first().click();
  await openSearch(page);
  await page.locator(".search-input").fill(token);
  await expect(page.locator(".search-group-name", { hasText: "alpha-search.txt" })).toBeVisible();
  await expect(page.locator(".search-group-name", { hasText: "beta-search.txt" })).toHaveCount(0);

  await page.keyboard.press("Control+Tab");
  await expect(chips.nth(1)).toHaveClass(/\bactive\b/);
  await expect(page.locator(".search-input")).toHaveValue(token);
  await expect(page.locator(".search-group-name", { hasText: "beta-search.txt" })).toBeVisible();
  await expect(page.locator(".search-group-name", { hasText: "alpha-search.txt" })).toHaveCount(0);
  await page.keyboard.press("F4");
  await expect(page.locator(".editor")).toHaveAttribute("data-active-file", /beta-search\.txt$/);
});

test("seeds from a highlight in the agent transcript, over an older editor selection", async ({
  page,
}) => {
  await awaitEditorReady(page);
  await createSession(page, { branch: "e2e/find-transcript-seed", provider: "fake-acp" });

  // The fake agent echoes the prompt, so the transcript holds a word that exists in the worktree.
  const surface = page.locator('[data-surface="structured-agent"]');
  const composer = surface.locator("[data-agent-composer] textarea");
  await composer.click();
  await composer.fill("console");
  await composer.press("Enter");
  const message = surface.locator(".agent-entry-message.agent-tone-assistant").last();
  await expect(message).toContainText("echo: console");

  // An editor selection left behind first: the fresher transcript highlight has to win, since Monaco keeps
  // its selection when the pane loses focus and would otherwise seed a word the user stopped looking at.
  await selectGreet(page);
  await dragOverWord(page, message, "console");

  await openSearch(page);
  await expect(page.locator(".search-input")).toHaveValue("console");
  await expect(page.locator(".search-summary")).toHaveText("1 match in 1 file");
});

// A terminal's highlight: xterm paints its selection onto a canvas rather than into the DOM, so the pane
// registers its own reader. The claude pane's text comes from the scripted fake, so the row the drag lands on
// is the same on every OS; a shell prompt would not be.
test.describe("a highlight in a terminal pane", () => {
  test.use({
    fakeScript: {
      steps: [
        { op: "print", text: "greet" },
        { op: "sleep", ms: 600_000 },
      ],
    },
  });

  // Drag across the claude pane's `greet`, the gesture a user makes. The row and the cell grid come from the
  // xterm buffer, the only place a canvas-rendered terminal's text exists.
  async function dragOverPrintedWord(page: Page): Promise<void> {
    const read = (): Promise<{ row: number; columns: number; rows: number } | null> =>
      page.evaluate(() => {
        const entry = Object.entries(window.__WEAVIE_TERMINALS__ ?? {}).find(([key]) =>
          key.endsWith(":claude"),
        );
        if (entry === undefined) {
          return null;
        }
        const term = entry[1];
        const buffer = term.buffer.active;
        for (let index = 0; index < term.rows; index++) {
          if (buffer.getLine(buffer.viewportY + index)?.translateToString(true) === "greet") {
            return { row: index, columns: term.cols, rows: term.rows };
          }
        }
        return null;
      });
    await expect.poll(read).not.toBeNull();
    const grid = await read();
    const box = await page
      .locator('.terminal-surface[data-kind="terminal:claude"] .xterm-screen')
      .boundingBox();
    if (grid === null || box === null) {
      throw new Error("the claude pane never printed `greet` onto a laid-out row");
    }
    const y = box.y + ((grid.row + 0.5) * box.height) / grid.rows;
    await page.mouse.move(box.x + 1, y);
    await page.mouse.down();
    await page.mouse.move(box.x + (box.width / grid.columns) * 11, y, { steps: 8 });
    await page.mouse.up();
  }

  test("seeds the search", async ({ page }) => {
    await dragOverPrintedWord(page);
    await openSearch(page);
    await expect(page.locator(".search-input")).toHaveValue("greet");
    await expect(page.locator(".search-summary")).toHaveText("2 matches in 1 file");
  });
});
