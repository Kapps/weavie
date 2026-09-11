import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { clickEditorLine } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";

const baseline = Array.from({ length: 1_000 }, (_, index) => `line ${index}`).join("\n");
const paths = Array.from(
  { length: 20 },
  (_, index) => `wheel-${String(index).padStart(2, "0")}.txt`,
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
        baseline.replace(
          "line 500",
          `changed ${index} ${"wide ".repeat(index % 3 === 0 ? 200 : 1)}`,
        ),
      ),
    ),
  },
});

test("downward scrolling preserves file order when the focused review editor leaves the viewport", async ({
  page,
}) => {
  await page.locator(".editor-empty-review").click();
  const scroller = page.locator(".unified-review-diffs");
  await expect(scroller).toBeVisible();
  await clickEditorLine(page.locator(".unified-review-file .view-line").first());
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
  for (let notch = 0; notch < 80; notch++) {
    await page.mouse.wheel(0, 200);
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
});
