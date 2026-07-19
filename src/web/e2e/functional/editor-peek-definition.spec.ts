import type { Page } from "@playwright/test";
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

// The viewport point of `word`'s middle character on `line`, read from Monaco's own layout.
async function wordPoint(
  page: Page,
  line: number,
  word: string,
): Promise<{ x: number; y: number }> {
  const point = await page.evaluate(
    (target) => {
      const editor = (window as WeavieWindow).__WEAVIE_EDITOR__;
      const model = editor?.getModel();
      const dom = editor?.getDomNode();
      if (editor === undefined || model === null || model === undefined || dom === null) {
        return null;
      }
      const index = model.getLineContent(target.line).indexOf(target.word);
      if (index < 0) {
        return null;
      }
      const column = index + 1 + Math.floor(target.word.length / 2);
      const spot = editor.getScrolledVisiblePosition({ lineNumber: target.line, column });
      if (spot === null || dom === undefined) {
        return null;
      }
      const rect = dom.getBoundingClientRect();
      return { x: rect.left + spot.left, y: rect.top + spot.top + spot.height / 2 };
    },
    { line, word },
  );
  if (point === null) {
    throw new Error(`no "${word}" on line ${line}`);
  }
  return point;
}

async function altClick(page: Page, point: { x: number; y: number }): Promise<void> {
  await page.keyboard.down("Alt");
  await page.mouse.click(point.x, point.y);
  await page.keyboard.up("Alt");
}

test("alt+click on a symbol opens the definition peek inline, and Escape closes it", async ({
  page,
}) => {
  await focusEditor(page, "hello.ts");
  await registerGreetDefinition(page);

  await altClick(page, await wordPoint(page, 5, "greet"));
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

  const point = await wordPoint(page, 5, "greet");
  await page.mouse.click(point.x, point.y);
  await page.keyboard.press("Alt+F12");
  await expect(page.locator(".monaco-editor .peekview-widget")).toBeVisible();
});

test("alt+click without a definition provider leaves Monaco's multicursor gesture alone", async ({
  page,
}) => {
  await focusEditor(page, "notes.txt");

  await altClick(page, await wordPoint(page, 1, "plain"));
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
  await altClick(page, await wordPoint(page, 5, "greet"));
  await page.waitForFunction(
    () => ((window as WeavieWindow).__WEAVIE_EDITOR__?.getSelections() ?? []).length === 3,
  );
  await expect(page.locator(".monaco-editor .peekview-widget")).toHaveCount(0);
});
