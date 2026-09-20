import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";

const source = Array.from({ length: 100 }, (_, index) => `public class Value${index} {}`).join(
  "\n",
);
const paths = Array.from(
  { length: 20 },
  (_, index) => `Semantic${String(index).padStart(2, "0")}.cs`,
);

async function semanticRequests(workspace: string): Promise<string[]> {
  const content = await readFile(join(workspace, ".fake-lsp-semantic-requests"), "utf8");
  return content.trim().split(/\r?\n/);
}

test.describe("review semantic-token requests", () => {
  test.use({
    workspaceSeed: {
      run: async (workspace) => {
        await Promise.all(paths.map((path) => writeFile(join(workspace, path), source)));
      },
    },
    fakeScript: {
      steps: paths.flatMap((path) => appliedEdit(path, source.replace("Value50", "Changed50"))),
    },
  });

  test("highlighting and spelling reuse semantic results when review editors remount", async ({
    page,
    weavie,
  }) => {
    await expect(page.locator(".editor-empty-review")).toContainText("20");
    await page.locator(".editor-empty-review").click();
    await expect(page.locator(".unified-review-file .monaco-editor").first()).toBeVisible();
    const scrollbar = page.getByRole("scrollbar", { name: "Review scroll position" });
    for (const key of ["Home", "End", "Home", "End", "Home"]) {
      await scrollbar.press(key);
      const path = key === "Home" ? paths[0]! : paths.at(-1)!;
      await expect
        .poll(async () =>
          (await semanticRequests(weavie.workspace).catch(() => [])).some((uri) =>
            uri.endsWith(path),
          ),
        )
        .toBe(true);
      await page.waitForTimeout(1_500);
    }
    const requests = await semanticRequests(weavie.workspace);
    expect(requests.length).toBeGreaterThan(1);
    expect(
      requests.length,
      "each unchanged model is requested only once across consumers and remounts",
    ).toBe(new Set(requests).size);

    await page
      .locator(".unified-review-file .view-line")
      .first()
      .click({ position: { x: 40, y: 8 } });
    await page.keyboard.press("Home");
    await page.keyboard.insertText("/* Updated */ ");
    await page.waitForTimeout(1_500);
    await expect
      .poll(
        async () =>
          (await semanticRequests(weavie.workspace)).filter((uri) => uri.endsWith(paths[0]!))
            .length,
      )
      .toBe(2);
  });
});

test.describe("semantic highlighting ownership", () => {
  test.use({
    fakeScript: {
      steps: [
        { op: "mcp", tool: "setSetting", args: { key: "editor.spellCheck", value: false } },
        ...appliedEdit("semantic.ownership", "export const value = 1;\n"),
      ],
    },
  });

  test("one Monaco controller requests highlighting for a model", async ({ page }) => {
    await page.clock.install();
    await page.evaluate(() => {
      document.documentElement.dataset.highlightingRequests = "0";
      const languages = window.__WEAVIE_MONACO__!.languages;
      languages.register({ id: "semantic-ownership", extensions: [".ownership"] });
      languages.registerDocumentSemanticTokensProvider("semantic-ownership", {
        getLegend: () => ({ tokenTypes: ["variable"], tokenModifiers: [] }),
        provideDocumentSemanticTokens: () => {
          document.documentElement.dataset.highlightingRequests = String(
            Number(document.documentElement.dataset.highlightingRequests) + 1,
          );
          return { data: new Uint32Array([0, 13, 5, 0, 0]) };
        },
        releaseDocumentSemanticTokens: () => {},
      });
    });
    await page.locator(".editor-empty-review").click();
    await expect(page.locator(".unified-review-file .monaco-editor")).toBeVisible();
    await page.clock.runFor(1_500);
    await expect(page.locator("html")).toHaveAttribute("data-highlighting-requests", "1");
  });
});
