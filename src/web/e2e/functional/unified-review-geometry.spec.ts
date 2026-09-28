import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { SESSION_FILE_SCHEME } from "../../src/editor/session-uri-scheme";
import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";

const files = [
  { path: "geometry-empty.txt", original: "removed empty file contents", changed: "" },
  {
    path: "geometry-eof.txt",
    original: "preserved line\nremoved at EOF",
    changed: "preserved line\n",
  },
  {
    path: "geometry-rtl.txt",
    original: "שלום עולם مرحبا بالعالم\nunchanged\n",
    changed: "שלום שינוי עולם مرحبا تغيير بالعالم\nunchanged\n",
  },
  {
    path: "geometry-wrap.txt",
    original: `start\n${"original words ".repeat(90)}\nend`,
    changed: `start\n${"wrapped changed words ".repeat(90)}\nend`,
  },
];

test.use({
  workspaceSeed: {
    run: async (workspace) => {
      await Promise.all(files.map((file) => writeFile(join(workspace, file.path), file.original)));
    },
  },
  fakeScript: {
    steps: [
      { op: "mcp", tool: "setSetting", args: { key: "editor.wordWrap", value: "on" } },
      ...files.flatMap((file) => appliedEdit(file.path, file.changed)),
    ],
  },
});

test("empty, EOF, RTL, and wrapped reviews activate at the exact prepared height", async ({
  page,
}) => {
  await expect(page.locator(".editor-empty-review")).toContainText("4");
  await page.locator(".editor-empty-review").click();
  await expect(page.locator('.review-adaptive-body[aria-busy="false"]')).toHaveCount(4);
  for (const file of files) {
    await page
      .getByRole("scrollbar", { name: "Review scroll position", exact: true })
      .press("Home");
    const section = page.locator(".unified-review-file", {
      has: page.locator(".unified-review-file-name", { hasText: file.path }),
    });
    const body = section.locator(".review-adaptive-body");
    await expect(body).toHaveAttribute("data-presentation", "passive");
    const prepared = await body.evaluate((element) => element.getBoundingClientRect().height);
    expect(prepared).toBeGreaterThan(0);
    await page.locator(".unified-review-tree-row.file", { hasText: file.path }).click();
    await expect(body).toHaveAttribute("data-presentation", "live");
    expect(await body.evaluate((element) => element.getBoundingClientRect().height)).toBe(prepared);
    await expect(section.locator(".review-adaptive-notice")).toHaveCount(0);
    const value = await page.evaluate((path) => {
      const monaco = (window as unknown as { __WEAVIE_MONACO__: typeof import("monaco-editor") })
        .__WEAVIE_MONACO__;
      const editor = monaco.editor
        .getEditors()
        .find(
          (candidate) =>
            candidate.getModel()?.uri.path.endsWith(`/${path}`) &&
            candidate.getDomNode()?.closest(".review-adaptive-live"),
        );
      if (!editor) throw new Error("Live review editor missing");
      return editor.getModel()!.getValue();
    }, file.path);
    expect(value).toBe(file.changed);
  }
});

test("global injected decorations repaint passive rows and survive live handoff", async ({
  page,
}) => {
  await expect(page.locator(".editor-empty-review")).toContainText("4");
  await page.locator(".editor-empty-review").click();
  await expect(page.locator('.review-adaptive-body[aria-busy="false"]')).toHaveCount(4);
  const path = "geometry-rtl.txt";
  const section = page.locator(".unified-review-file", {
    has: page.locator(".unified-review-file-name", { hasText: path }),
  });
  await page.evaluate(
    ({ path, scheme }) => {
      const monaco = (window as unknown as { __WEAVIE_MONACO__: typeof import("monaco-editor") })
        .__WEAVIE_MONACO__;
      const models = monaco.editor
        .getModels()
        .filter((model) => model.uri.scheme === scheme && model.uri.path.endsWith(`/${path}`));
      if (models.length !== 1) throw new Error("Expected one shared working model");
      models[0]!.deltaDecorations(
        [],
        [
          {
            range: new monaco.Range(1, 1, 1, 1),
            options: {
              description: "Test provider injected label",
              showIfCollapsed: true,
              before: {
                content: "injected-hint-label ",
                inlineClassName: "test-injected-hint",
                inlineClassNameAffectsLetterSpacing: true,
              },
            },
          },
        ],
      );
    },
    { path, scheme: SESSION_FILE_SCHEME },
  );
  await expect(section.locator(".test-injected-hint")).toHaveCount(1);
  const body = section.locator(".review-adaptive-body");
  const prepared = await body.evaluate((element) => element.getBoundingClientRect().height);
  await page.locator(".unified-review-tree-row.file", { hasText: path }).click();
  await expect(body).toHaveAttribute("data-presentation", "live");
  await expect(section.locator(".review-adaptive-live .test-injected-hint")).toBeVisible();
  expect(await body.evaluate((element) => element.getBoundingClientRect().height)).toBe(prepared);
});
