import type { JSHandle, Page } from "@playwright/test";
import {
  activeSessionSlot,
  createSession,
  expectRevealed,
  openFile,
  openSearch,
  pressDocumentEnd,
  runCommand,
} from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { awaitReviewSet } from "../harness/navigator";
import { appliedEdit } from "../harness/review";
import type { EditorHandle, WeavieWindow } from "../harness/weavie-window";

const sourceName = "z-review.ts";
const sourceLines = [
  ...Array.from({ length: 79 }, (_, index) => `export const value${index} = ${index};`),
  'greet("review departure");',
];

test.use({
  fakeScript: {
    steps: [
      ...appliedEdit("a-review.ts", "export const first = true;\n"),
      ...appliedEdit(sourceName, sourceLines.join("\n")),
    ],
  },
});

async function reviewState(page: Page): Promise<{ selections: unknown; scrollTop: number }> {
  return page.evaluate((name) => {
    const editors = (window as WeavieWindow).__WEAVIE_MONACO__?.editor.getEditors() as
      | EditorHandle[]
      | undefined;
    const editor = editors?.find(
      (candidate) =>
        candidate !== (window as WeavieWindow).__WEAVIE_EDITOR__ &&
        candidate.getModel()?.uri.path.endsWith(`/${name}`),
    );
    const scroller = document.querySelector(".unified-review-diffs");
    if (editor === undefined || scroller === null) throw new Error("Review is not mounted");
    return { selections: editor.getSelections(), scrollTop: scroller.scrollTop };
  }, sourceName);
}

async function prepareDeparture(page: Page): Promise<void> {
  await awaitReviewSet(page, ["a-review.ts", sourceName]);
  await openFile(page, "notes.txt");
  await page.locator(".editor-review-open").click();
  await page.locator(".unified-review-tree-row.file", { hasText: sourceName }).click();
  const section = page.locator(".unified-review-file", {
    has: page.locator(".unified-review-file-name", { hasText: sourceName }),
  });
  await section
    .locator(".view-line")
    .first()
    .click({ position: { x: 4, y: 4 } });
  await pressDocumentEnd(page);
  const departure = section.locator(".view-line", { hasText: "review departure" });
  await expect(departure).toBeInViewport();
  await page.keyboard.press("Home");
  await page.keyboard.press("Shift+End");
  await expect.poll(async () => (await reviewState(page)).scrollTop).toBeGreaterThan(0);
}

interface DefinitionGate {
  requested: boolean;
  delivered: boolean;
  release(): void;
}

async function registerDefinition(
  page: Page,
  source: string,
  destination: string,
  held: boolean,
): Promise<JSHandle<DefinitionGate>> {
  return page.evaluateHandle(
    ({ name, target, held }) => {
      const monaco = (window as unknown as { __WEAVIE_MONACO__: typeof import("monaco-editor") })
        .__WEAVIE_MONACO__;
      const pending = Promise.withResolvers<void>();
      const gate = { requested: false, delivered: false, release: () => pending.resolve() };
      if (!held) gate.release();
      if (monaco === undefined) throw new Error("Monaco is not mounted");
      monaco.languages.registerDefinitionProvider("*", {
        provideDefinition: async (model) => {
          if (!model.uri.path.endsWith(`/${name}`)) return [];
          gate.requested = true;
          await pending.promise;
          gate.delivered = true;
          const prefix = "weavie-session:";
          const owner = JSON.parse(decodeURIComponent(model.uri.fragment.slice(prefix.length))) as {
            hostPath: string;
          };
          owner.hostPath = owner.hostPath.replace(/[^/]+$/, target);
          return [
            {
              uri: model.uri.with({
                path: model.uri.path.replace(/[^/]+$/, target),
                fragment: `${prefix}${encodeURIComponent(JSON.stringify(owner))}`,
              }),
              range: { startLineNumber: 1, startColumn: 17, endLineNumber: 1, endColumn: 22 },
            },
          ];
        },
      });
      return gate;
    },
    { name: source, target: destination, held },
  );
}

for (const invocation of ["keyboard", "palette", "context menu"] as const) {
  test(`${invocation} definition navigation restores the exact unified review departure with Back`, async ({
    page,
  }) => {
    await prepareDeparture(page);
    await registerDefinition(page, sourceName, "hello.ts", false);
    const departure = await reviewState(page);
    if (invocation === "keyboard") await page.keyboard.press("F12");
    else if (invocation === "context menu") {
      await page.locator(".unified-review .view-line", { hasText: "review departure" }).click({
        button: "right",
        position: { x: 40, y: 4 },
      });
      await page
        .locator(".context-menu-item")
        .filter({ hasText: /^Go to Definition/ })
        .click();
    } else {
      await page.keyboard.press("ControlOrMeta+Shift+p");
      await page.locator(".tb-omnibar-input").fill(">Go to Definition");
      await expect(
        page.locator(".tb-omnibar-row", { hasText: "Go to Definition" }).first(),
      ).toBeVisible();
      await page.locator(".tb-omnibar-input").press("Enter");
    }
    await expectRevealed(page, "hello.ts", 1);
    await expect(page.locator(".unified-review")).toHaveCount(0);

    await runCommand(page, "Go Back");
    await expect(page.locator(".unified-review")).toBeVisible();
    await expect(page.locator(".weavie-inline-stack-name")).toHaveText(sourceName);
    await expect.poll(() => reviewState(page)).toEqual(departure);
    await expect(
      page.locator(".unified-review .view-line", { hasText: "review departure" }),
    ).toBeInViewport();

    await page.keyboard.press("ControlOrMeta+Shift+p");
    await page.locator(".tb-omnibar-input").fill(">Go Forward");
    await page.locator(".tb-omnibar-input").press("Enter");
    await expect(page.locator(".unified-review")).toHaveCount(0);
    await expectRevealed(page, "hello.ts", 1);
    await page.locator(".editor-tab", { hasText: "Review Changes" }).click();
    await expect(
      page.locator(".unified-review .view-line", { hasText: "review departure" }),
    ).toBeInViewport();
    await expect.poll(() => reviewState(page)).toEqual(departure);
    await page.locator(".editor-tab", { hasText: "notes.txt" }).click();
    await expect(page.locator(".editor-tab.active")).toContainText("notes.txt");
    await page.locator(".editor-tab", { hasText: "Review Changes" }).click();
    await expect(
      page.locator(".unified-review .view-line", { hasText: "review departure" }),
    ).toBeInViewport();
    await expect.poll(() => reviewState(page)).toEqual(departure);
    await expect(
      page.locator(".unified-review").getByRole("textbox", { name: "Editor content" }).last(),
    ).toBeFocused();
  });
}

test("same-file definition navigation preserves review locations in both directions", async ({
  page,
}) => {
  await prepareDeparture(page);
  await registerDefinition(page, sourceName, sourceName, false);
  const departure = await reviewState(page);
  await page.keyboard.press("F12");
  await expect(page.locator(".unified-review")).toBeVisible();
  await expect
    .poll(async () => (await reviewState(page)).selections)
    .toEqual([
      {
        startLineNumber: 1,
        startColumn: 17,
        endLineNumber: 1,
        endColumn: 17,
        selectionStartLineNumber: 1,
        selectionStartColumn: 17,
        positionLineNumber: 1,
        positionColumn: 17,
      },
    ]);
  const destination = await reviewState(page);
  await runCommand(page, "Go Back");
  await expect.poll(() => reviewState(page)).toEqual(departure);
  await runCommand(page, "Go Forward");
  await expect.poll(() => reviewState(page)).toEqual(destination);
});

for (const originSurface of ["review", "file"] as const) {
  test(`a definition reply from a detached ${originSurface} cannot navigate the newly selected session`, async ({
    page,
  }) => {
    await prepareDeparture(page);
    if (originSurface === "file") await openFile(page, sourceName);
    const origin = await activeSessionSlot(page);
    const definition = await registerDefinition(page, sourceName, "hello.ts", true);
    await page.keyboard.press("F12");
    await expect.poll(() => definition.evaluate((gate) => gate.requested)).toBe(true);

    await createSession(page, { branch: "e2e/definition-owner", provider: "claude" });
    expect(await activeSessionSlot(page)).not.toBe(origin);
    await openFile(page, "notes.txt");
    await openFile(page, "README.md");
    const destinationPath = await page.locator(".editor").getAttribute("data-active-file");
    const selection = await page.evaluate(() => window.__WEAVIE_EDITOR__?.getSelections());
    await definition.evaluate(async (gate) => {
      gate.release();
      await new Promise<void>((resolve) =>
        requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
      );
    });
    await expect.poll(() => definition.evaluate((gate) => gate.delivered)).toBe(true);
    await expect(page.locator(".editor")).toHaveAttribute("data-active-file", destinationPath!);
    expect(await page.evaluate(() => window.__WEAVIE_EDITOR__?.getSelections())).toEqual(selection);
    await expect(page.locator(".editor-tab", { hasText: "hello.ts" })).toHaveCount(0);

    await runCommand(page, "Go Back");
    await expectRevealed(page, "notes.txt", 1);
    await runCommand(page, "Go Forward");
    await expect(page.locator(".editor")).toHaveAttribute("data-active-file", destinationPath!);
  });
}

test("document symbols preview, cancel and commit against the originating review editor", async ({
  page,
}) => {
  await prepareDeparture(page);
  const departure = await reviewState(page);
  await page.evaluate((name) => {
    const monaco = (window as unknown as { __WEAVIE_MONACO__: typeof import("monaco-editor") })
      .__WEAVIE_MONACO__;
    monaco.languages.registerDocumentSymbolProvider("*", {
      provideDocumentSymbols: (model) =>
        model.uri.path.endsWith(`/${name}`)
          ? [
              {
                name: "reviewOwnedSymbol",
                detail: "",
                kind: monaco.languages.SymbolKind.Constant,
                tags: [],
                range: { startLineNumber: 1, startColumn: 1, endLineNumber: 1, endColumn: 25 },
                selectionRange: {
                  startLineNumber: 1,
                  startColumn: 14,
                  endLineNumber: 1,
                  endColumn: 20,
                },
              },
            ]
          : [],
    });
  }, sourceName);
  const input = page.locator(".tb-omnibar-input");
  for (const action of ["cancel", "commit"] as const) {
    await page.keyboard.press("ControlOrMeta+p");
    await input.fill("@reviewOwnedSymbol");
    await expect(page.locator(".tb-omnibar-row", { hasText: "reviewOwnedSymbol" })).toBeVisible();
    await input.press("ArrowDown");
    await expect
      .poll(async () => (await reviewState(page)).selections)
      .toEqual([
        {
          startLineNumber: 1,
          startColumn: 14,
          endLineNumber: 1,
          endColumn: 20,
          selectionStartLineNumber: 1,
          selectionStartColumn: 14,
          positionLineNumber: 1,
          positionColumn: 20,
        },
      ]);
    await input.press(action === "cancel" ? "Escape" : "Enter");
    if (action === "cancel") await expect.poll(() => reviewState(page)).toEqual(departure);
  }
  await expect(page.locator(".unified-review")).toBeVisible();
  await runCommand(page, "Go Back");
  await expect.poll(() => reviewState(page)).toEqual(departure);
});

test("search preview leaving unified review transfers editor commands to the displayed file", async ({
  page,
}) => {
  await prepareDeparture(page);
  await registerDefinition(page, "notes.txt", "hello.ts", false);
  await openSearch(page);
  const input = page.locator(".search-input");
  await expect(input).toHaveValue('greet("review departure");');
  await input.fill("just plain text");
  await expect(page.locator(".search-row")).toHaveCount(1);
  await input.press("ArrowDown");
  await expectRevealed(page, "notes.txt", 1);
  await expect(page.locator(".unified-review")).toHaveCount(0);
  await expect(input).toBeFocused();
  await input.press("Escape");
  await page
    .locator(".editor .view-line")
    .first()
    .click({ position: { x: 4, y: 4 } });
  await runCommand(page, "Go to Definition");
  await expectRevealed(page, "hello.ts", 1);
});
