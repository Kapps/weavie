import { SESSION_FILE_SCHEME } from "../../src/editor/session-uri-scheme";
import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";
import { reviewEditor } from "../harness/review-renderer";
import { reviewScroll } from "../harness/review-scroll";

const paths = ["a-ready.txt", "b-ready.txt"];
test.use({
  fakeScript: {
    steps: paths.flatMap((path) =>
      appliedEdit(path, Array.from({ length: 80 }, (_, line) => `${path} line ${line}`).join("\n")),
    ),
  },
});

for (const cancel of [false, true]) {
  test(`navigation to a retained pending file ${cancel ? "cancels on user scrolling" : "focuses once after fresh preparation"}`, async ({
    page,
  }) => {
    await expect(page.locator(".editor-empty-review")).toContainText("2");
    await page.locator(".editor-empty-review").click();
    await expect(page.locator('.review-adaptive-body[aria-busy="false"]')).toHaveCount(2);
    const section = page.locator(".unified-review-file", {
      has: page.locator(".unified-review-file-name", { hasText: paths[1] }),
    });
    const gate = await page.evaluateHandle(
      ({ path, scheme }) => {
        const monaco = (window as unknown as { __WEAVIE_MONACO__: typeof import("monaco-editor") })
          .__WEAVIE_MONACO__;
        const model = monaco.editor
          .getModels()
          .find((model) => model.uri.scheme === scheme && model.uri.path.endsWith(`/${path}`));
        if (!model) throw new Error("Expected the prepared working model");
        const grammar =
          Promise.withResolvers<import("monaco-editor").languages.EncodedTokensProvider>();
        const language = "review-pending-grammar";
        monaco.languages.register({ id: language });
        const subscriptions = [monaco.languages.setTokensProvider(language, grammar.promise)];
        const tokenState: import("monaco-editor").languages.IState = {
          clone() {
            return this;
          },
          equals(other) {
            return this === other;
          },
        };
        const state = { creates: 0, focuses: 0 };
        subscriptions.push(
          monaco.editor.onDidCreateEditor((editor) => {
            subscriptions.push(
              editor.onDidChangeModel(() => {
                if (editor.getModel() === model) state.creates++;
              }),
            );
            subscriptions.push(
              editor.onDidFocusEditorText(() => {
                if (editor.getModel() === model) state.focuses++;
              }),
            );
          }),
        );
        monaco.editor.setModelLanguage(model, language);
        return {
          release: () =>
            grammar.resolve({
              getInitialState: () => tokenState,
              tokenizeEncoded: (_line, endState) => ({
                tokens: new Uint32Array([0, monaco.languages.getEncodedLanguageId(language)]),
                endState,
              }),
            }),
          state: () => state,
          dispose: () => {
            for (const subscription of subscriptions) subscription.dispose();
          },
        };
      },
      { path: paths[1]!, scheme: SESSION_FILE_SCHEME },
    );
    try {
      await expect(section.locator(".review-adaptive-body")).toHaveAttribute("aria-busy", "true");
      await page.locator(".unified-review-tree-row.file", { hasText: paths[1] }).click();
      await page.evaluate(
        () =>
          new Promise<void>((resolve) =>
            requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
          ),
      );
      await expect(section.locator(".review-adaptive-body")).toHaveAttribute("aria-busy", "true");
      await expect(reviewEditor(section)).toHaveCount(0);
      expect(await gate.evaluate((gate) => gate.state())).toEqual({ creates: 0, focuses: 0 });
      const scrollbar = page.getByRole("scrollbar", {
        name: "Review scroll position",
        exact: true,
      });
      if (cancel) {
        await scrollbar.press("Home");
        await expect.poll(() => reviewScroll(page).then(({ top }) => top)).toBe(0);
        await expect(scrollbar).toBeFocused();
      }
      await gate.evaluate((gate) => gate.release());
      await expect(section.locator(".review-adaptive-body")).toHaveAttribute("aria-busy", "false");
      if (cancel) {
        await expect(scrollbar).toBeFocused();
        expect((await reviewScroll(page)).top).toBe(0);
        expect((await gate.evaluate((gate) => gate.state())).focuses).toBe(0);
      } else {
        await expect(reviewEditor(section)).toBeVisible();
        await expect
          .poll(() => gate.evaluate((gate) => gate.state()))
          .toEqual({ creates: 1, focuses: 1 });
        await expect(
          reviewEditor(section).getByRole("textbox", { name: "Editor content" }),
        ).toBeFocused();
        await expect(
          reviewEditor(section).locator(".view-line", { hasText: /^b-ready\.txt\sline\s0$/ }),
        ).toBeInViewport();
      }
    } finally {
      await gate.evaluate((gate) => {
        gate.release();
        gate.dispose();
      });
    }
  });
}
