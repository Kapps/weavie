import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";
import { readReviewScroll, reviewScroll } from "../harness/review-scroll";

const source = Array.from(
  { length: 7_000 },
  (_, index) => `export const value${index} = ${index};`,
);
const baseline = source.join("\n");
const paths = Array.from(
  { length: 30 },
  (_, index) => `wheel-${String(index).padStart(2, "0")}.ts`,
);

test.use({
  workspaceSeed: {
    run: async (workspace) => {
      await Promise.all(paths.map((path) => writeFile(join(workspace, path), baseline)));
    },
  },
  fakeScript: {
    steps: paths.flatMap((path, index) =>
      appliedEdit(
        path,
        source
          .map((line, lineIndex) =>
            lineIndex >= 3_500 && lineIndex < 3_550
              ? `export const value${lineIndex} = "changed ${index} ${lineIndex} ${"wide ".repeat(index % 3 === 0 ? 200 : 1)}";`
              : line,
          )
          .join("\n"),
      ),
    ),
  },
});

test("wheel scrolling preserves file order and geometry as review editors remount", async ({
  page,
}) => {
  test.slow();
  await expect(page.locator(".editor-empty-review")).toContainText(`${paths.length}`);
  await page.locator(".editor-empty-review").click();
  const scroller = page.locator(".unified-review-diffs");
  await expect(scroller).toBeVisible();
  await expect(page.locator(".weavie-inline-stack-sub")).toContainText(`file 1/${paths.length}`);
  await page.locator(".unified-review-tree-row.file").first().click();
  const firstEditor = page.locator(".unified-review-file .monaco-editor").first();
  await firstEditor
    .locator(".view-line")
    .first()
    .click({ position: { x: 4, y: 4 } });
  await expect(firstEditor).toHaveClass(/focused/);
  const observation = await scroller.evaluateHandle((element) => {
    const samples: number[][] = [];
    const sample = () => {
      const viewport = element.getBoundingClientRect();
      const files = [...element.querySelectorAll<HTMLElement>(".unified-review-file")]
        .filter((section) => {
          const bounds = section.getBoundingClientRect();
          return bounds.bottom > viewport.top && bounds.top < viewport.bottom;
        })
        .map((section) => Number(section.dataset.index));
      samples.push(files);
    };
    const observer = new MutationObserver(sample);
    observer.observe(element, {
      subtree: true,
      attributes: true,
      attributeFilter: ["aria-valuenow"],
    });
    sample();
    return samples;
  });
  await scroller.hover();
  // Monaco normalizes wheel notches; its native Alt modifier accelerates this long traversal.
  await page.keyboard.down("Alt");
  const scrollbar = await page
    .getByRole("scrollbar", { name: "Review scroll position" })
    .elementHandle();
  if (scrollbar === null) throw new Error("Review scrollbar is missing");
  const position = () => scrollbar.evaluate(readReviewScroll);
  let scroll = await position();
  while (scroll.top < scroll.maximum) {
    await page.mouse.wheel(0, 400);
    await page.evaluate(
      () =>
        new Promise<void>((resolve) =>
          requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
        ),
    );
    scroll = await position();
  }
  await expect(scroller).toBeFocused();
  const samples = await observation.jsonValue();
  const visited = [...new Set(samples.flat())].sort((a, b) => a - b);
  expect(visited).toEqual(paths.map((_, index) => index + 1));
  const firstVisible = samples.flatMap((sample) => sample.slice(0, 1));
  expect(firstVisible).toEqual([...firstVisible].sort((a, b) => a - b));
  const settledHeight = scroll.maximum;
  const reverseHeights = new Set<number>();
  while (scroll.top > 0) {
    await page.mouse.wheel(0, -400);
    scroll = await position();
    reverseHeights.add(scroll.maximum);
  }
  expect([...reverseHeights]).toEqual([settledHeight]);
  await page.keyboard.up("Alt");
  await expect.poll(() => reviewScroll(page).then(({ top }) => top)).toBe(0);
  const rows = page.locator(".unified-review-tree-row.file");
  await rows.first().focus();
  await page.keyboard.press("End");
  await expect(rows.last()).toBeFocused();
  await expect(rows.last()).toBeInViewport();
  await page.keyboard.press("Home");
  await expect(rows.first()).toBeFocused();
  await expect(rows.first()).toBeInViewport();
});
