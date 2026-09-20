import type { Locator, Page } from "@playwright/test";
import { expectRevealed } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { awaitReviewSet } from "../harness/navigator";
import { appliedEdit } from "../harness/review";

const sourceName = "definition-review.ts";
const baseline = [
  "export const hiddenDefinition = (value: string) => value;",
  ...Array.from({ length: 178 }, (_, index) => `export const untouched${index} = ${index};`),
  'hiddenDefinition("original call");',
];

test.use({
  fakeScript: {
    steps: [
      { op: "edit", path: `{{WORKSPACE}}/${sourceName}`, content: baseline.join("\n") },
      ...appliedEdit(
        sourceName,
        baseline
          .map((line, index) => (index === 179 ? 'hiddenDefinition("review call");' : line))
          .join("\n"),
      ),
      ...appliedEdit(
        "z-more-review.ts",
        Array.from({ length: 100 }, (_, index) => `export const pending${index} = ${index};`).join(
          "\n",
        ),
      ),
    ],
  },
});

async function prepareDefinition(page: Page): Promise<Locator> {
  await awaitReviewSet(page, [sourceName, "z-more-review.ts"]);
  await page.locator(".editor-empty-review").click();
  const section = page.locator(".unified-review-file", {
    has: page.locator(".unified-review-file-name", { hasText: sourceName }),
  });
  await expect(
    section.locator(".view-line", { hasText: "export const hiddenDefinition" }),
  ).toHaveCount(0);
  const word = section
    .locator(".view-line", { hasText: "review call" })
    .locator("span", { hasText: "hiddenDefinition" })
    .last();
  await expect(word).toBeInViewport();
  await page.evaluate((name) => {
    const monaco = (window as unknown as { __WEAVIE_MONACO__: typeof import("monaco-editor") })
      .__WEAVIE_MONACO__;
    monaco.languages.registerDefinitionProvider(
      { language: "typescript", exclusive: true },
      {
        provideDefinition: (model) =>
          model.uri.path.endsWith(`/${name}`)
            ? [
                {
                  uri: model.uri,
                  range: { startLineNumber: 1, startColumn: 14, endLineNumber: 1, endColumn: 30 },
                },
              ]
            : [],
      },
    );
  }, sourceName);
  return word;
}

test("Go to Definition opens an unchanged same-file definition hidden outside the review hunk", async ({
  page,
}) => {
  const word = await prepareDefinition(page);
  await word.click();
  await page.keyboard.press("F12");
  await expect(page.locator(".unified-review")).toHaveCount(0);
  await expectRevealed(page, sourceName, 1);
  await expect(
    page.locator(".editor .view-line", { hasText: "export const hiddenDefinition" }),
  ).toBeInViewport();
});

test("Alt+click shows an unchanged definition without leaving unified review, and Escape closes it", async ({
  page,
}) => {
  const word = await prepareDefinition(page);
  await word.click({ modifiers: ["Alt"] });
  const review = page.locator(".unified-review");
  const peek = review.locator(".peekview-widget");
  await expect(peek).toBeVisible();
  await expect(
    peek.locator(".monaco-editor .view-line", { hasText: "export const hiddenDefinition" }),
  ).toBeVisible();
  await expect(review).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(peek).toHaveCount(0);
  await expect(review).toBeVisible();
  await expect(word).toBeInViewport();
});
