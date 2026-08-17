import type { Locator, Page } from "@playwright/test";
import { openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Alt+Click on a symbol peeks its definition inline — the same embedded window Find All References uses —
// and Alt+F12 peeks at the cursor. The definition provider is mocked through __WEAVIE_MONACO__ (the harness
// bundles no language server), so these pin Weavie's gesture + command wiring and the widget opening, not
// LSP resolution. Where no provider can exist (plain text), the gesture must leave Monaco's built-in
// alt+click multicursor untouched.
//
// OPEN, UNRESOLVED — 2026-08-17: this file's windows shard 3/6 hung the full 60s test budget three times
// in one afternoon, each time on a different test here, always waiting on a wordToken(...).click() for
// hello.ts's "greet" token:
//   https://github.com/Kapps/weavie/actions/runs/32061153041/job/95483710320 (Alt+F12 test)
//   https://github.com/Kapps/weavie/actions/runs/32065959082/job/95499188918 (multicursor test)
//   https://github.com/Kapps/weavie/actions/runs/32067115833/job/95503117733 (multicursor test again,
//     AFTER closing the Alt+F12 test's peek below — ruling out that as the cause)
// First read as a frozen browser tab (run 1's trace screencast stopped producing frames ~7s in), but run 3's
// trace screencast kept producing frames at a steady ~270ms cadence for the entire 59s hang — the tab was
// alive and rendering the whole time. Its mid-hang screenshots show the hello.ts tab open, data-active-file
// presumably already stamped (focusEditor's own asserts passed), and the editor pane completely blank — no
// view-lines ever painted. That points at a real gap between "the model swap landed" (editor-host.ts's
// reflectActiveFile, driven by onDidChangeModel) and Monaco actually rendering that model's content, not at
// this spec's locators or gesture wiring — closing the Alt+F12 test's leaked peek widget (below) was worth
// doing regardless but did not stop the recurrence, it just moved to the next test in the file. Root cause
// is still open; look at monaco-setup.ts's automaticLayout / editor-host.ts's model-swap path before
// touching this file again over a Windows-only hang here.

import type { WeavieWindow } from "../harness/weavie-window";

async function focusEditor(page: Page, name: string): Promise<void> {
  await openFile(page, name);
  await page.locator(".monaco-editor .view-lines").first().click();
  await expect(page.locator('.editor-surface[data-kind="editor"]')).toHaveClass(/\bactive\b/);
}

// Every position resolves to hello.ts's `greet` declaration on line 1 — enough to open a real peek.
async function registerGreetDefinition(page: Page): Promise<void> {
  await page.evaluate(() => {
    const monaco = (window as WeavieWindow).__WEAVIE_MONACO__;
    if (monaco === undefined) {
      throw new Error("monaco handle not available");
    }
    monaco.languages.registerDefinitionProvider("*", {
      provideDefinition: (model) => {
        const column = model.getLineContent(1).indexOf("greet") + 1;
        return [
          {
            uri: model.uri,
            range: {
              startLineNumber: 1,
              startColumn: column,
              endLineNumber: 1,
              endColumn: column + "greet".length,
            },
          },
        ];
      },
    });
  });
}

// The rendered token for `word` on the line containing `lineText`.
//
// Monaco gives each token its own span, so the gesture can address the word itself instead of a viewport
// coordinate computed from the editor's layout. That matters: the editor's offset in the window keeps moving
// while the shell lays out and the session starts, and a coordinate measured before it settles addresses a
// place the line has left by the time the click lands — which reads as "the peek never opened" rather than
// "we clicked the wrong pixel". Waiting for the reading to stop changing wasn't enough either, because it can
// sit stably wrong for many frames while the chrome is still assembling. Handing the target to Playwright
// puts its actionability checks — visible, stable, receives pointer events — at the moment of the click.
// `last()` takes the innermost span holding the word: a highlighted line nests one span per token inside a
// span for the whole line, while plain text is a single span — this addresses the text either way, and never
// the full-width line element, whose centre can land past the end of the code.
function wordToken(page: Page, lineText: string, word: string): Locator {
  return page
    .locator(".view-line", { hasText: lineText })
    .locator("span", { hasText: word })
    .last();
}

async function altClick(word: Locator): Promise<void> {
  await word.click({ modifiers: ["Alt"] });
}

test("alt+click on a symbol opens the definition peek inline, and Escape closes it", async ({
  page,
}) => {
  await focusEditor(page, "hello.ts");
  await registerGreetDefinition(page);

  await altClick(wordToken(page, "const message = greet", "greet"));
  const peek = page.locator(".monaco-editor .peekview-widget");
  await expect(peek).toBeVisible();
  // The peek embeds its own editor showing the definition's file — the small window into the file.
  await expect(peek.locator(".monaco-editor").first()).toBeVisible();

  await page.keyboard.press("Escape");
  await expect(peek).toHaveCount(0);
});

// Closes its own peek before returning (like the test above) rather than leaving the page/context teardown
// to reclaim it — good hygiene on its own, though the file-level note above found it wasn't the actual
// cause of this file's Windows-only hang.
test("Alt+F12 peeks the definition of the symbol at the cursor", async ({ page }) => {
  await focusEditor(page, "hello.ts");
  await registerGreetDefinition(page);

  await wordToken(page, "const message = greet", "greet").click();
  await page.keyboard.press("Alt+F12");
  const peek = page.locator(".monaco-editor .peekview-widget");
  await expect(peek).toBeVisible();

  await page.keyboard.press("Escape");
  await expect(peek).toHaveCount(0);
});

test("alt+click without a definition provider leaves Monaco's multicursor gesture alone", async ({
  page,
}) => {
  await focusEditor(page, "notes.txt");

  await altClick(wordToken(page, "just plain text", "plain"));
  // Monaco's default alt+click added a second cursor — the gesture declined and didn't swallow the click.
  await page.waitForFunction(
    () => ((window as WeavieWindow).__WEAVIE_EDITOR__?.getSelections() ?? []).length === 2,
  );
  await expect(page.locator(".monaco-editor .peekview-widget")).toHaveCount(0);
});

test("alt+click during a multicursor session adds a cursor instead of peeking", async ({
  page,
}) => {
  await focusEditor(page, "hello.ts");
  await registerGreetDefinition(page);

  // Seed a two-cursor session; alt+clicking a word must then stay Monaco's add-cursor, not a peek.
  await page.evaluate(() => {
    const editor = (window as WeavieWindow).__WEAVIE_EDITOR__;
    if (editor === undefined) {
      throw new Error("editor handle not available");
    }
    editor.setSelections([
      {
        selectionStartLineNumber: 1,
        selectionStartColumn: 1,
        positionLineNumber: 1,
        positionColumn: 1,
      },
      {
        selectionStartLineNumber: 2,
        selectionStartColumn: 3,
        positionLineNumber: 2,
        positionColumn: 3,
      },
    ]);
  });
  await altClick(wordToken(page, "const message = greet", "greet"));
  await page.waitForFunction(
    () => ((window as WeavieWindow).__WEAVIE_EDITOR__?.getSelections() ?? []).length === 3,
  );
  await expect(page.locator(".monaco-editor .peekview-widget")).toHaveCount(0);
});
