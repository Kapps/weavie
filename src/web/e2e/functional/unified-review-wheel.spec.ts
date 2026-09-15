import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";

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
    element.addEventListener("scroll", sample);
    sample();
    return samples;
  });
  await scroller.hover();
  for (let notch = 0; notch < 200; notch++) {
    await page.mouse.wheel(0, 400);
    await page.evaluate(
      () =>
        new Promise<void>((resolve) =>
          requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
        ),
    );
  }
  await expect(scroller).toBeFocused();
  const samples = await observation.jsonValue();
  const visited = [...new Set(samples.flat())].sort((a, b) => a - b);
  expect(visited).toEqual(paths.map((_, index) => index + 1));
  const firstVisible = samples.flatMap((sample) => sample.slice(0, 1));
  for (let index = 1; index < firstVisible.length; index++) {
    expect(firstVisible[index]).toBeGreaterThanOrEqual(firstVisible[index - 1]!);
  }
  const settledHeight = await scroller.evaluate((element) => element.scrollHeight);
  for (let notch = 0; notch < 200; notch++) {
    await page.mouse.wheel(0, -400);
    expect(await scroller.evaluate((element) => element.scrollHeight)).toBe(settledHeight);
  }
  await expect.poll(() => scroller.evaluate((element) => element.scrollTop)).toBe(0);
});
