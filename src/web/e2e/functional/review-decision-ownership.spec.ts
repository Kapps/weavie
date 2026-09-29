import { access, readFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { openFile, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { type HeldResponse, holdHostResponse } from "../harness/held-response";
import { awaitReviewSet } from "../harness/navigator";
import { appliedEdit } from "../harness/review";
import { reviewEditor } from "../harness/review-renderer";

const source = "a-decision.txt";
const next = "z-decision.txt";
const contents = {
  [source]: "First created review file\nIts last pending change\n",
  [next]: "Second created review file\nIts pending change stays untouched\n",
};
const replies = new WeakMap<Page, HeldResponse>();

test.use({
  fakeScript: {
    steps: Object.entries(contents).flatMap(([path, content]) => appliedEdit(path, content)),
  },
  preNavigate: {
    run: async (page) => {
      replies.set(page, await holdHostResponse(page));
    },
  },
});

const section = (page: Page, name: string) =>
  page.locator(".unified-review-file", {
    has: page.locator(".unified-review-file-name", { hasText: name }),
  });

async function observeNextEntry(page: Page) {
  return page.evaluateHandle((name) => {
    const monaco = (window as unknown as { __WEAVIE_MONACO__: typeof import("monaco-editor") })
      .__WEAVIE_MONACO__;
    const state = { binds: 0, focusEdges: 0 };
    const subscriptions: import("monaco-editor").IDisposable[] = [];
    const isNext = (editor: import("monaco-editor").editor.ICodeEditor): boolean =>
      editor.getModel()?.uri.path.endsWith(`/${name}`) === true;
    const observe = (editor: import("monaco-editor").editor.ICodeEditor): void => {
      subscriptions.push(
        editor.onDidChangeModel(() => {
          if (isNext(editor)) state.binds++;
        }),
        editor.onDidFocusEditorText(() => {
          if (isNext(editor)) state.focusEdges++;
        }),
      );
    };
    for (const editor of monaco.editor.getEditors()) {
      if (isNext(editor)) throw new Error("The next file is already active before the decision");
      observe(editor);
    }
    subscriptions.push(
      monaco.editor.onDidCreateEditor((editor) => {
        if (isNext(editor)) state.binds++;
        observe(editor);
      }),
    );
    return {
      state: () => state,
      dispose: () => {
        for (const subscription of subscriptions) subscription.dispose();
      },
    };
  }, next);
}

for (const surface of ["file", "unified review"] as const) {
  for (const action of ["keep", "revert"] as const) {
    for (const newerPalette of [false, true]) {
      test(`${surface} ${action} completion ${newerPalette ? "preserves a newer palette" : "advances once after its response"}`, async ({
        page,
        weavie,
      }) => {
        await awaitReviewSet(page, [source, next]);
        await openFile(page, source);
        if (surface === "unified review") {
          await page.locator(".editor-review-open").click();
          await page.locator(".unified-review-tree-row.file", { hasText: source }).click();
          await expect(reviewEditor(section(page, source))).toBeVisible();
        }
        const entered = await observeNextEntry(page);
        try {
          const reply = replies.get(page)!;
          const operation =
            action === "revert" ? "revertFile" : surface === "file" ? "keepHunk" : "keepFile";
          reply.hold((message) => message.feature === "review" && message.name === operation);
          if (surface === "unified review") {
            await section(page, source).locator(`.unified-review-file-action.${action}`).click();
          } else if (action === "keep") {
            await expect(page.locator(".weavie-inline-pending-keep")).toHaveCount(1);
            await page.locator(".weavie-inline-pending-keep").click();
          } else await runCommand(page, "Revert File (Review)");
          if (action === "revert") {
            await page
              .locator(".confirm-dialog")
              .getByRole("button", { name: "Revert file", exact: true })
              .click();
          }
          await expect
            .poll(() => reply.received()?.payload)
            .toEqual({
              sourceDeleted: action === "revert",
              sourceHasReview: action === "keep",
              next: { path: join(weavie.workspace, next), line: 1 },
            });

          const path = join(weavie.workspace, source);
          if (action === "revert") {
            await expect
              .poll(async () => {
                try {
                  await access(path);
                  return false;
                } catch (error) {
                  if ((error as NodeJS.ErrnoException).code !== "ENOENT") throw error;
                  return true;
                }
              })
              .toBe(true);
            await expect
              .poll(() =>
                page.evaluate((name) => {
                  const monaco = (
                    window as unknown as { __WEAVIE_MONACO__: typeof import("monaco-editor") }
                  ).__WEAVIE_MONACO__;
                  return monaco.editor
                    .getEditors()
                    .filter((editor) => editor.getModel()?.uri.path.endsWith(`/${name}`)).length;
                }, source),
              )
              .toBe(0);
            if (surface === "file") {
              await expect(page.locator(".editor-tab", { hasText: source })).toHaveCount(0);
            } else {
              const retired = section(page, source);
              await expect(retired.locator(".unified-review-status")).toHaveText("Reviewed");
              await expect(retired.locator(".unified-review-file-toggle")).toHaveAttribute(
                "aria-expanded",
                "false",
              );
              await expect(retired.locator(".unified-review-rejection pre")).toHaveText(
                contents[source],
              );
            }
          } else {
            expect(await readFile(path, "utf8")).toBe(contents[source]);
            if (surface === "file") {
              await expect(page.locator(".editor")).toHaveAttribute("data-active-file", path);
              await expect(page.locator(".weavie-inline-pending-keep")).toHaveCount(0);
              await expect(page.locator(".weavie-inline-accepted-undo")).toHaveCount(1);
              await expect(page.locator(".weavie-inline-accepted")).toHaveCount(
                contents[source].split("\n").length,
              );
            } else {
              await expect(section(page, source).locator(".unified-review-status")).toHaveText(
                "Reviewed",
              );
            }
          }
          expect(await entered.evaluate((counter) => counter.state())).toEqual({
            binds: 0,
            focusEdges: 0,
          });

          if (newerPalette) {
            await page.locator(".tb-omnibar-input").click();
            await page.locator(".tb-omnibar-input").fill(">Go Back");
            await expect(page.locator(".tb-omnibar-input")).toBeFocused();
          }
          await reply.release();
          if (newerPalette) {
            await expect(page.locator(".tb-omnibar-input")).toBeFocused();
            await expect(page.locator(".tb-omnibar-input")).toHaveValue(">Go Back");
            await expect(page.locator(".tb-omnibar-box")).toHaveClass(/\bopen\b/);
            expect(await entered.evaluate((counter) => counter.state())).toEqual({
              binds: 0,
              focusEdges: 0,
            });
          } else {
            await expect(page.locator(".weavie-inline-stack-name")).toHaveText(next);
            if (surface === "file") {
              await expect(page.locator(".editor")).toHaveAttribute(
                "data-active-file",
                join(weavie.workspace, next),
              );
              await expect(
                page.locator(".editor-surface").getByRole("textbox", { name: "Editor content" }),
              ).toBeFocused();
            } else {
              await expect(page.locator(".unified-review")).toBeVisible();
              await expect(
                reviewEditor(section(page, next)).getByRole("textbox", { name: "Editor content" }),
              ).toBeFocused();
            }
            await expect.poll(() => entered.evaluate((counter) => counter.state().binds)).toBe(1);
            // Changing the model of a focused widget does not emit another focus edge.
            expect(
              await entered.evaluate((counter) => counter.state().focusEdges),
            ).toBeLessThanOrEqual(1);
          }
          expect(await readFile(join(weavie.workspace, next), "utf8")).toBe(contents[next]);
        } finally {
          await entered.evaluate((counter) => counter.dispose());
          await entered.dispose();
        }
      });
    }
  }
}

test.describe("unloaded completed review files", () => {
  const middle = "m-kept.txt";
  test.use({
    fakeScript: {
      steps: [
        ...appliedEdit(source, contents[source]),
        ...appliedEdit(middle, "Already kept before this client reload\n"),
        ...appliedEdit(next, contents[next]),
      ],
    },
  });

  test("completion skips a kept file whose model was not loaded after reload", async ({
    page,
    weavie,
  }) => {
    await awaitReviewSet(page, [source, middle, next]);
    await openFile(page, middle);
    await runCommand(page, "Keep File (Review)");
    await expect(page.locator(".editor")).toHaveAttribute(
      "data-active-file",
      join(weavie.workspace, middle),
    );
    await expect(page.locator(".weavie-inline-accepted-undo")).toHaveCount(1);
    await openFile(page, source);
    await page.reload();
    await expect(page.locator("#splash")).toHaveCount(0);
    await openFile(page, source);
    const middleLoaded = () =>
      page.evaluate((name) => {
        const monaco = (window as unknown as { __WEAVIE_MONACO__: typeof import("monaco-editor") })
          .__WEAVIE_MONACO__;
        return monaco.editor.getModels().some((model) => model.uri.path.endsWith(`/${name}`));
      }, middle);
    expect(await middleLoaded()).toBe(false);
    const reply = replies.get(page)!;
    reply.hold((message) => message.feature === "review" && message.name === "keepHunk");
    await expect(page.locator(".weavie-inline-pending-keep")).toHaveCount(1);
    await page.locator(".weavie-inline-pending-keep").click();
    await expect
      .poll(() => reply.received()?.payload)
      .toEqual({
        sourceDeleted: false,
        sourceHasReview: true,
        next: { path: join(weavie.workspace, next), line: 1 },
      });
    await expect(page.locator(".weavie-inline-accepted-undo")).toHaveCount(1);
    await expect(page.locator(".editor")).toHaveAttribute(
      "data-active-file",
      join(weavie.workspace, source),
    );
    await reply.release();
    await expect(page.locator(".editor")).toHaveAttribute(
      "data-active-file",
      join(weavie.workspace, next),
    );
    await expect(page.locator(".weavie-inline-stack-name")).toHaveText(next);
    expect(await middleLoaded()).toBe(false);
    expect(await readFile(join(weavie.workspace, next), "utf8")).toBe(contents[next]);
  });
});
