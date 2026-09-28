import { readFile } from "node:fs/promises";
import { join } from "node:path";
import type { IPosition, editor as MonacoEditor } from "monaco-editor";
import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";
import { scrollReview } from "../harness/review-scroll";

const paths = ["a-widgets.ts", "b-widgets.ts"] as const;
const content = Array.from(
  { length: 300 },
  (_, index) => `export const value${index} = ${index};`,
).join("\n");

test.use({
  fakeScript: { steps: paths.flatMap((path) => appliedEdit(path, content)) },
});

test("scrolled review completions stay at the caret and belong to their editor", async ({
  page,
}) => {
  await expect(page.locator(".editor-empty-review")).toContainText("2");
  await page.evaluate(() => {
    window.__WEAVIE_MONACO__!.languages.registerCompletionItemProvider("typescript", {
      triggerCharacters: ["."],
      provideCompletionItems: (_model, position) => ({
        suggestions: [
          {
            label: "reviewCompletion",
            kind: 1,
            insertText: "reviewCompletion",
            range: {
              startLineNumber: position.lineNumber,
              endLineNumber: position.lineNumber,
              startColumn: position.column,
              endColumn: position.column,
            },
          },
        ],
      }),
    });
  });
  await page.locator(".editor-empty-review").click();
  for (const path of paths) {
    await scrollReview(page, "start");
    await page.locator(".unified-review-tree-row.file", { hasText: path }).click();
    const section = page.locator(".unified-review-file", {
      has: page.locator(".unified-review-file-name", { hasText: path }),
    });
    await expect(section.locator(".view-line").first()).toBeVisible();
    const scrollbar = page.getByRole("scrollbar", { name: "Review scroll position" });
    await scrollbar.press("PageDown");
    await section
      .locator(".view-line")
      .nth(3)
      .click({ position: { x: 10, y: 8 } });
    await page.keyboard.press("Home");
    await page.keyboard.type(".");
    const widget = page.locator(".suggest-widget.visible");
    await expect(widget).toContainText("reviewCompletion");
    await expect(widget).toBeInViewport();
    const bounds = await widget.boundingBox();
    const caret = await section.locator(".cursor").first().boundingBox();
    if (bounds === null || caret === null) throw new Error("Completion or caret is missing");
    expect(Math.abs(bounds.x - caret.x)).toBeLessThan(10);
    expect(
      Math.min(
        Math.abs(bounds.y - caret.y - caret.height),
        Math.abs(bounds.y + bounds.height - caret.y),
      ),
    ).toBeLessThan(10);
    expect(
      await widget.evaluate((element) => element.closest(".unified-review-virtual-list")),
    ).toBeNull();
    await widget.getByRole("option", { name: /reviewCompletion/ }).click();
    await expect(section.locator(".view-line", { hasText: "reviewCompletion" })).toBeVisible();
    const focused = await page.evaluate(() =>
      (window.__WEAVIE_MONACO__!.editor.getEditors() as MonacoEditor.IStandaloneCodeEditor[])
        .filter((editor) => editor.hasWidgetFocus())
        .map((editor) => editor.getModel()?.uri.path),
    );
    expect(focused).toHaveLength(1);
    expect(focused[0]).toMatch(new RegExp(`${path.replace(".", "\\.")}$`));
    await section.locator(".unified-review-file-toggle").click();
    await expect(section.locator(".monaco-editor")).toHaveCount(0);
    await expect(page.locator(".suggest-widget")).toHaveCount(0);
    await expect
      .poll(async () =>
        page.evaluate(
          () =>
            document.querySelectorAll(".unified-review-overflow-widgets").length -
            document.querySelectorAll(".unified-review-editor-viewport > .monaco-editor").length,
        ),
      )
      .toBe(0);
  }
});

test("scrolled review rename accepts and cancels while a definition peek is open", async ({
  page,
  weavie,
}) => {
  await expect(page.locator(".editor-empty-review")).toContainText("2");
  await page.evaluate(() => {
    const location = (model: MonacoEditor.ITextModel, position: IPosition) => {
      const word = model.getWordAtPosition(position)!;
      return {
        text: word.word,
        range: {
          startLineNumber: position.lineNumber,
          endLineNumber: position.lineNumber,
          startColumn: word.startColumn,
          endColumn: word.endColumn,
        },
      };
    };
    window.__WEAVIE_MONACO__!.languages.registerRenameProvider("typescript", {
      resolveRenameLocation: location,
      provideRenameEdits: (model, position, newName) => ({
        edits: [
          {
            resource: model.uri,
            versionId: model.getVersionId(),
            textEdit: { range: location(model, position).range, text: newName },
          },
        ],
      }),
    });
    window.__WEAVIE_MONACO__!.languages.registerDefinitionProvider("typescript", {
      provideDefinition: (model) => ({
        uri: model.uri,
        range: { startLineNumber: 1, endLineNumber: 1, startColumn: 14, endColumn: 20 },
      }),
    });
  });
  await page.locator(".editor-empty-review").click();
  await page.locator(".unified-review-tree-row.file", { hasText: paths[1] }).click();
  const section = page.locator(".unified-review-file", {
    has: page.locator(".unified-review-file-name", { hasText: paths[1] }),
  });
  await expect(section.locator(".view-line").first()).toBeVisible();
  await page.getByRole("scrollbar", { name: "Review scroll position" }).press("PageDown");
  const word = section
    .locator(".view-line")
    .nth(3)
    .locator("span", { hasText: /^value\d+$/ })
    .last();
  const original = await word.textContent();
  await word.click({ modifiers: ["Alt"] });
  const peek = page.locator(".peekview-widget");
  await expect(peek).toBeVisible();
  await word.click();
  await page.keyboard.press("F2");
  const input = page.locator(".rename-input:visible");
  await expect(input).toBeFocused();
  await input.fill("cancelledRename");
  await page.keyboard.press("Escape");
  await expect(input).toBeHidden();
  await expect(peek).toBeVisible();
  await expect(word).toHaveText(original!);
  await page.keyboard.press("F2");
  await expect(input).toBeFocused();
  await input.fill("renamedReviewValue");
  await page.keyboard.press("Enter");
  await expect(input).toBeHidden();
  await expect(
    section.locator(".view-line:not(.peekview-widget .view-line)", {
      hasText: "renamedReviewValue",
    }),
  ).toBeVisible();
  await expect
    .poll(() => readFile(join(weavie.workspace, paths[1]!), "utf8"))
    .toContain("renamedReviewValue");
});
