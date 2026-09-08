import type { Locator, Page } from "@playwright/test";
import { awaitEditorLaidOut, clickIntoEditor, expectRevealed, openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// The harness definition provider pins gesture routing and the real peek widget, independently of LSP.
import type { WeavieWindow } from "../harness/weavie-window";

async function focusEditor(page: Page, name: string): Promise<void> {
  await openFile(page, name);
  await clickIntoEditor(page);
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
async function wordToken(page: Page, lineText: string, word: string): Promise<Locator> {
  await awaitEditorLaidOut(page);
  return page
    .locator(".view-line", { hasText: lineText })
    .locator("span", { hasText: word })
    .last();
}

async function altClick(word: Locator): Promise<void> {
  await word.click({ modifiers: ["Alt"] });
}

test("Mod+Alt+click on a symbol opens the definition peek inline, and Escape closes it", async ({
  page,
}) => {
  await focusEditor(page, "hello.ts");
  await registerGreetDefinition(page);

  await (await wordToken(page, "const message = greet", "greet")).click({
    modifiers: ["ControlOrMeta", "Alt"],
  });
  const peek = page.locator(".monaco-editor .peekview-widget");
  await expect(peek).toBeVisible();
  // The peek embeds its own editor showing the definition's file — the small window into the file.
  await expect(peek.locator(".monaco-editor").first()).toBeVisible();
  await expectRevealed(page, "hello.ts", 5);

  await page.keyboard.press("Escape");
  await expect(peek).toHaveCount(0);
  await expectRevealed(page, "hello.ts", 5);
});

test("Alt+F12 peeks the definition of the symbol at the cursor", async ({ page }) => {
  await focusEditor(page, "hello.ts");
  await registerGreetDefinition(page);

  await (await wordToken(page, "const message = greet", "greet")).click();
  await page.keyboard.press("Alt+F12");
  await expect(page.locator(".monaco-editor .peekview-widget")).toBeVisible();
});

test("alt+click on a definition-backed symbol adds a cursor without peeking", async ({ page }) => {
  await focusEditor(page, "hello.ts");
  await registerGreetDefinition(page);

  await altClick(await wordToken(page, "const message = greet", "greet"));
  await page.waitForFunction(
    () => ((window as WeavieWindow).__WEAVIE_EDITOR__?.getSelections() ?? []).length === 2,
  );
  await expect(page.locator(".monaco-editor .peekview-widget")).toHaveCount(0);
});

test("alt+click without a definition provider leaves Monaco's multicursor gesture alone", async ({
  page,
}) => {
  await focusEditor(page, "notes.txt");

  await altClick(await wordToken(page, "just plain text", "plain"));
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
  // `wordToken` re-waits for editor layout itself (see its doc comment) — no separate guard needed here.
  await altClick(await wordToken(page, "const message = greet", "greet"));
  await page.waitForFunction(
    () => ((window as WeavieWindow).__WEAVIE_EDITOR__?.getSelections() ?? []).length === 3,
  );
  await expect(page.locator(".monaco-editor .peekview-widget")).toHaveCount(0);
});

test("Mod+click on a symbol navigates to its definition", async ({ page }) => {
  await focusEditor(page, "hello.ts");
  await registerGreetDefinition(page);

  await (await wordToken(page, "const message = greet", "greet")).click({
    modifiers: ["ControlOrMeta"],
  });
  await expectRevealed(page, "hello.ts", 1);
  await expect(page.locator(".monaco-editor .peekview-widget")).toHaveCount(0);
});

test("Mod+Alt drag away from a symbol and back does not peek", async ({ page }) => {
  await focusEditor(page, "hello.ts");
  await registerGreetDefinition(page);
  const word = await wordToken(page, "const message = greet", "greet");
  await word.hover();
  const bounds = await word.boundingBox();
  if (bounds === null) throw new Error("symbol has no bounds");
  const x = bounds.x + bounds.width / 2;
  const y = bounds.y + bounds.height / 2;
  await page.keyboard.down("ControlOrMeta");
  await page.keyboard.down("Alt");
  await page.mouse.move(x, y);
  await page.mouse.down();
  await page.mouse.move(x + 60, y, { steps: 5 });
  await page.mouse.move(x, y, { steps: 5 });
  await page.mouse.up();
  await page.keyboard.up("Alt");
  await page.keyboard.up("ControlOrMeta");
  await expect(page.locator(".monaco-editor .peekview-widget")).toHaveCount(0);
});
