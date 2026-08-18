import type { Locator, Page } from "@playwright/test";
import { openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Alt+Click on a symbol peeks its definition inline — the same embedded window Find All References uses —
// and Alt+F12 peeks at the cursor. The definition provider is mocked through __WEAVIE_MONACO__ (the harness
// bundles no language server), so these pin Weavie's gesture + command wiring and the widget opening, not
// LSP resolution. Where no provider can exist (plain text), the gesture must leave Monaco's built-in
// alt+click multicursor untouched.

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

test("Alt+F12 peeks the definition of the symbol at the cursor", async ({ page }) => {
  await focusEditor(page, "hello.ts");
  await registerGreetDefinition(page);

  await wordToken(page, "const message = greet", "greet").click();
  await page.keyboard.press("Alt+F12");
  await expect(page.locator(".monaco-editor .peekview-widget")).toBeVisible();
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
  // Flaked on windows-latest, e2e (windows) shard 3/6: 2026-08-18 05:11 UTC
  // (https://github.com/Kapps/weavie/actions/runs/32096266021/job/95602915943). This click hung the
  // full 60s budget then reported "Target page, context or browser has been closed" — same signature
  // as the #626/#627 investigations, but the trace ruled out both: screencast frames kept arriving
  // continuously the whole time (the tab wasn't frozen, unlike #626), and the viewport-layout dump
  // showed the container at its correct, non-collapsed size (742x709, unlike the 0-height case #627
  // fixed in openFile) yet Monaco had rendered only one blank line against a 7-line model. That matches
  // #627's own latch theory — a 0-height moment made Monaco clamp to 1 line and never re-measured after
  // recovering — just triggered by this test's `editor.setSelections` call re-revealing the selection
  // rather than by the initial file open, which is the only call site #627 guards. Left as a dated note
  // rather than a guess-fix: the real fix likely belongs on the recovery side (Monaco/the editor should
  // re-layout on its own once its container is healthy again) rather than adding a wait at yet another
  // call site.
  await altClick(wordToken(page, "const message = greet", "greet"));
  await page.waitForFunction(
    () => ((window as WeavieWindow).__WEAVIE_EDITOR__?.getSelections() ?? []).length === 3,
  );
  await expect(page.locator(".monaco-editor .peekview-widget")).toHaveCount(0);
});
