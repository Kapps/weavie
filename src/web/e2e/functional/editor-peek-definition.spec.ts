import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Locator, Page } from "@playwright/test";
import type { MessageEnvelope } from "../../src/messaging/message-envelope";
import { awaitEditorLaidOut, clickIntoEditor, openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Alt+Click on a symbol peeks its definition inline — the same embedded window Find All References uses —
// and Alt+F12 peeks at the cursor. The definition provider is mocked through __WEAVIE_MONACO__ (the harness
// bundles no language server), so these pin Weavie's gesture + command wiring and the widget opening, not
// LSP resolution. Where no provider can exist (plain text), the gesture must leave Monaco's built-in
// alt+click multicursor untouched.

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

// Address the innermost rendered token so Playwright checks its current layout at click time.
async function wordToken(page: Page, lineText: string, word: string): Promise<Locator> {
  await awaitEditorLaidOut(page);
  return page
    .locator(".view-line", { hasText: lineText })
    .locator("span", { hasText: word })
    .last();
}

async function altClick(page: Page, lineText: string, word: string): Promise<void> {
  await awaitEditorLaidOut(page);
  const point = await page.evaluate(
    ({ lineText, word }) => {
      const editor = window.__WEAVIE_EDITOR__;
      const model = editor?.getModel();
      const bounds = editor?.getDomNode()?.getBoundingClientRect();
      if (!editor || !model || !bounds) throw new Error("Editor is not ready");
      const line = model.getLinesContent().findIndex((text) => text.includes(lineText));
      if (line < 0) throw new Error(`Line not found: ${lineText}`);
      const start = model.getLineContent(line + 1).indexOf(word);
      if (start < 0) throw new Error(`Word not found: ${word}`);
      const position = editor.getScrolledVisiblePosition({
        lineNumber: line + 1,
        column: start + 1 + Math.floor(word.length / 2),
      });
      if (!position) throw new Error("Symbol is not visible");
      return { x: bounds.left + position.left, y: bounds.top + position.top + position.height / 2 };
    },
    { lineText, word },
  );
  // Alt hover replaces the token span with a link decoration; target its model position, not DOM identity.
  await page.mouse.move(point.x, point.y);
  await page.keyboard.down("Alt");
  await page.mouse.click(point.x, point.y);
  await page.keyboard.up("Alt");
}

test("alt+click on a symbol opens the definition peek inline, and Escape closes it", async ({
  page,
}) => {
  await focusEditor(page, "hello.ts");
  await registerGreetDefinition(page);

  await altClick(page, "const message = greet", "greet");
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

  await (await wordToken(page, "const message = greet", "greet")).click();
  await page.keyboard.press("Alt+F12");
  await expect(page.locator(".monaco-editor .peekview-widget")).toBeVisible();
});

test("alt+click without a definition provider leaves Monaco's multicursor gesture alone", async ({
  page,
}) => {
  await focusEditor(page, "notes.txt");

  await altClick(page, "just plain text", "plain");
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
  await altClick(page, "const message = greet", "greet");
  await page.waitForFunction(
    () => ((window as WeavieWindow).__WEAVIE_EDITOR__?.getSelections() ?? []).length === 3,
  );
  await expect(page.locator(".monaco-editor .peekview-widget")).toHaveCount(0);
});

const unopenedDefinitionLine = 501;

async function registerUnopenedDefinition(page: Page): Promise<void> {
  await page.evaluate((line) => {
    const monaco = (window as WeavieWindow).__WEAVIE_MONACO__;
    if (monaco === undefined) throw new Error("monaco handle not available");
    monaco.languages.registerDefinitionProvider("*", {
      provideDefinition: (model) => {
        const owner = JSON.parse(
          decodeURIComponent(model.uri.fragment.slice("weavie-session:".length)),
        );
        owner.hostPath = owner.hostPath.replace(/hello\.ts$/, "unopened-definition.ts");
        return [
          {
            uri: model.uri.with({
              path: model.uri.path.replace(/hello\.ts$/, "unopened-definition.ts"),
              fragment: `weavie-session:${encodeURIComponent(JSON.stringify(owner))}`,
            }),
            range: { startLineNumber: line, startColumn: 17, endLineNumber: line, endColumn: 22 },
          },
        ];
      },
    });
  }, unopenedDefinitionLine);
}

test.describe("unopened definitions", () => {
  let releaseRead: (() => void) | undefined;
  test.use({
    preNavigate: {
      run: async (page) => {
        releaseRead = undefined;
        await page.routeWebSocket("**/*", (socket) => {
          const server = socket.connectToServer();
          let requestId: string | undefined;
          socket.onMessage((data) => {
            const message = JSON.parse(data.toString()) as MessageEnvelope;
            if (
              message.feature === "files" &&
              message.name === "read" &&
              (message.payload as { path: string }).path.endsWith("unopened-definition.ts")
            ) {
              requestId = message.requestId;
            }
            server.send(data);
          });
          server.onMessage((data) => {
            const message = JSON.parse(data.toString()) as MessageEnvelope;
            if (requestId !== undefined && message.requestId === requestId) {
              releaseRead = () => socket.send(data);
            } else socket.send(data);
          });
        });
      },
    },
  });
  for (const gesture of ["Alt+click", "context-menu Peek"] as const) {
    test(`${gesture} renders a never-opened definition after its file read completes`, async ({
      page,
      weavie,
    }) => {
      await writeFile(
        join(weavie.workspace, "unopened-definition.ts"),
        `${"// Padding before the definition\n".repeat(unopenedDefinitionLine - 1)}export function greet() { return "UNOPENED_DEFINITION_CONTENT"; }\n`,
      );
      await focusEditor(page, "hello.ts");
      await registerUnopenedDefinition(page);
      expect(
        await page.evaluate(() => {
          const monaco = (
            window as unknown as { __WEAVIE_MONACO__: typeof import("monaco-editor") }
          ).__WEAVIE_MONACO__;
          return monaco.editor
            .getModels()
            .some((model) => model.uri.path.endsWith("unopened-definition.ts"));
        }),
      ).toBe(false);
      await expect(page.locator(".editor-tab", { hasText: "unopened-definition.ts" })).toHaveCount(
        0,
      );
      const word = await wordToken(page, "const message = greet", "greet");
      if (gesture === "Alt+click") {
        await altClick(page, "const message = greet", "greet");
      } else {
        await word.click({ button: "right" });
        await page.locator(".context-menu-item", { hasText: "Peek Definition" }).click();
      }
      const peek = page.locator(".monaco-editor .peekview-widget");
      await expect(peek).toBeVisible();
      await expect.poll(() => releaseRead !== undefined).toBe(true);
      if (releaseRead === undefined) throw new Error("Target file read was not intercepted");
      releaseRead();
      await expect(
        peek.locator(".view-line", { hasText: "UNOPENED_DEFINITION_CONTENT" }),
      ).toBeVisible();
      await expect
        .poll(() =>
          peek.locator(".preview > .monaco-editor").evaluate((node) => {
            const pane = node.closest(".split-view-view");
            const body = node.closest(".body");
            if (pane === null || body === null)
              throw new Error("Peek preview has no layout container");
            return {
              width: node.clientWidth - pane.clientWidth,
              height: node.clientHeight - body.getBoundingClientRect().height,
            };
          }),
        )
        .toEqual({ width: 0, height: 0 });
      await expect(page.locator(".editor-tab", { hasText: "unopened-definition.ts" })).toHaveCount(
        0,
      );
    });
  }
});

// Flaked in CI on 2026-09-12 08:04 UTC on an unrelated PR
// (https://github.com/Kapps/weavie/actions/runs/34682062091/job/103522660783): the cursor stayed "text" for
// the entire 15s poll instead of ever becoming "pointer". Pulled the trace/viewport/console artifacts rather
// than guessing from the assertion — the target span stayed present and stable across every retry (not the
// DOM-remount/detach pattern already tracked in #868 for a different test in this file), and layout/console
// were clean. The pointer cursor and `.goto-definition-link` are Monaco's own built-in alt+hover gesture, not
// Weavie source, so the stuck state points at Monaco-internal hover/link recomputation timing rather than
// anything in this file. No other shard in that run hit it, and I lacked permission to re-run the job to
// confirm transience. Not yet safely fixable blind — no code change made.
test("Alt hovering a definition-backed symbol advertises its link and hand cursor", async ({
  page,
}) => {
  await focusEditor(page, "hello.ts");
  await registerGreetDefinition(page);
  const word = await wordToken(page, "const message = greet", "greet");
  const bounds = await word.boundingBox();
  if (bounds === null) throw new Error("Symbol has no bounds");
  await page.keyboard.down("Alt");
  await word.hover();
  await expect.poll(() => word.evaluate((node) => getComputedStyle(node).cursor)).toBe("pointer");
  await expect(page.locator(".goto-definition-link")).toBeVisible();
  await page.keyboard.up("Alt");
  await page.keyboard.down("ControlOrMeta");
  await page.mouse.click(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
  await page.keyboard.up("ControlOrMeta");
  await expect
    .poll(() => page.evaluate(() => window.__WEAVIE_EDITOR__?.getPosition()?.lineNumber))
    .toBe(1);
  await expect(page.locator(".peekview-widget")).toHaveCount(0);
});

for (const change of ["release Alt", "press Shift"] as const) {
  test(`Alt hover clears when the user ${change}`, async ({ page }) => {
    await focusEditor(page, "hello.ts");
    await registerGreetDefinition(page);
    const word = await wordToken(page, "const message = greet", "greet");
    await page.keyboard.down("Alt");
    await word.hover();
    await expect(page.locator(".goto-definition-link")).toBeVisible();
    if (change === "release Alt") await page.keyboard.up("Alt");
    else await page.keyboard.down("Shift");
    await expect(page.locator(".goto-definition-link")).toHaveCount(0);
    await expect.poll(() => word.evaluate((node) => getComputedStyle(node).cursor)).toBe("text");
  });
}

for (const cancel of ["release Alt after mouse down", "drag away and back"] as const) {
  test(`Alt click does not peek after ${cancel}`, async ({ page }) => {
    await focusEditor(page, "hello.ts");
    await registerGreetDefinition(page);
    const word = await wordToken(page, "const message = greet", "greet");
    await word.hover();
    const bounds = await word.boundingBox();
    if (bounds === null) throw new Error("Symbol has no bounds");
    const x = bounds.x + bounds.width / 2,
      y = bounds.y + bounds.height / 2;
    await page.keyboard.down("Alt");
    await page.mouse.move(x, y);
    await expect(page.locator(".goto-definition-link")).toBeVisible();
    await page.mouse.down();
    if (cancel === "release Alt after mouse down") {
      await page.keyboard.up("Alt");
      await expect(page.locator(".goto-definition-link")).toHaveCount(0);
    } else {
      await page.mouse.move(x + 60, y, { steps: 5 });
      await page.mouse.move(x, y, { steps: 5 });
    }
    await page.mouse.up();
    await page.keyboard.up("Alt");
    await expect(page.locator(".peekview-widget")).toHaveCount(0);
    if (cancel === "release Alt after mouse down") {
      await expect
        .poll(() => page.evaluate(() => window.__WEAVIE_EDITOR__?.getSelections()?.length))
        .toBe(2);
    }
  });
}
