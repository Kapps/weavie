import { expect, test } from "../harness/fixtures";
import { awaitReviewSet } from "../harness/navigator";
import { appliedEdit } from "../harness/review";
import { scrollReview } from "../harness/review-scroll";

// Past Monaco's 300k-line large-file threshold, where tokenization and wrapping are off.
const largeName = "generated.ts";
const baseline = Array.from(
  { length: 300_001 },
  (_, index) => `export const line${index + 1} = 0;`,
);
const changed = baseline.map((line, index) =>
  index === 150_000 ? "export const changed = 1;" : line,
);
// Whole new files below it, tall enough that scrolling to the end unmounts the large file's editor.
const fillers = ["z1.txt", "z2.txt", "z3.txt"];
const filler = Array.from({ length: 80 }, (_, index) => `filler ${index}`).join("\n");

test.use({
  fakeScript: {
    steps: [
      { op: "edit", path: `{{WORKSPACE}}/${largeName}`, content: baseline.join("\n") },
      ...appliedEdit(largeName, changed.join("\n")),
      ...fillers.flatMap((path) => appliedEdit(path, filler)),
    ],
  },
});

test("a huge file shows only its change in context and warns about tokenization once", async ({
  page,
}) => {
  test.setTimeout(90_000);
  await awaitReviewSet(page, [largeName, ...fillers]);
  await page.evaluate(() => {
    const seen = { warnings: 0 };
    (window as unknown as { __largeWarnings__: typeof seen }).__largeWarnings__ = seen;
    new MutationObserver((records) => {
      for (const record of records)
        for (const node of record.addedNodes)
          if (node instanceof HTMLElement && node.textContent?.includes("tokenization"))
            seen.warnings++;
    }).observe(document.body, { childList: true, subtree: true });
  });
  const warnings = () =>
    page.evaluate(
      () =>
        (window as unknown as { __largeWarnings__: { warnings: number } }).__largeWarnings__
          .warnings,
    );
  await page.locator(".editor-empty-review").click();
  const section = page.locator(".unified-review-file", {
    has: page.locator(".unified-review-file-name", { hasText: largeName }),
  });
  const expectCollapsed = async (): Promise<void> => {
    await expect(section.locator(".unified-review-gap")).toHaveText([
      "Show 149997 unchanged lines",
      "Show 149997 unchanged lines",
    ]);
    await expect(section.locator(".line-numbers")).toHaveText([
      "149998",
      "149999",
      "150000",
      "150001",
      "150002",
      "150003",
      "150004",
    ]);
  };
  await expectCollapsed();
  await expect.poll(warnings).toBe(1);

  await scrollReview(page, "end");
  await expect(section.locator(".monaco-editor")).toHaveCount(0);
  await scrollReview(page, "start");
  await expectCollapsed();
  // Monaco's warner contribution starts after first render; instantiate it now so it has had its say.
  await section.locator(".monaco-editor").evaluate((node) => {
    const monaco = (window as unknown as { __WEAVIE_MONACO__: typeof import("monaco-editor") })
      .__WEAVIE_MONACO__;
    const editor = monaco.editor.getEditors().find((editor) => editor.getDomNode() === node)!;
    editor.getContribution("editor.contrib.largeFileOptimizationsWarner");
    return new Promise((resolve) => requestAnimationFrame(resolve));
  });
  expect(await warnings()).toBe(1);
});
