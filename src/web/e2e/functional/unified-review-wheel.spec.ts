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
  // Flake (macOS only): 2026-09-16 02:14 UTC, run
  // https://github.com/Kapps/weavie/actions/runs/35046360832/job/104637538434 — this test hit
  // "Test timeout of 180000ms exceeded" / "Target page, context or browser has been closed" on the
  // release e2e's macOS leg. Root cause: each wheel notch is a real CDP round trip, and the wide-line
  // files' word-wrapped content pushes total scroll height past 70,000px, so a 400px notch kept this
  // test scrolling ~370 times total (~68s locally on Linux, leaving only ~24% headroom under the 90s
  // Linux "slow" budget — comfortably over budget on a slower/shared macOS runner). Fix: widened the
  // notch to 1,000px, which still stays safely under the shortest un-widened file's rendered height
  // (~1,076px from 56 collapsed-context lines) so no file's viewport window can be jumped clean over,
  // while roughly halving the round trips (measured ~44s locally, 4/4 runs green).
  const notchSize = 1000;
  const maxNotches = 200;
  // A stall must hold for several consecutive notches before the loop calls the scroller settled: the
  // virtualizer only refines an item's estimated height once it is actually scrolled to, so scrollTop
  // can sit still for a single notch while a newly-mounted file's real geometry lands, then resume
  // moving on the next.
  const settleStreak = 3;
  const untilSettled = async (
    delta: number,
    afterNotch?: (state: { top: number; height: number }) => void,
  ): Promise<void> => {
    let top = -1;
    let stalled = 0;
    for (let notch = 0; notch < maxNotches && stalled < settleStreak; notch++) {
      await page.mouse.wheel(0, delta);
      const state = await scroller.evaluate(
        (element) =>
          new Promise<{ top: number; height: number }>((resolve) =>
            requestAnimationFrame(() =>
              requestAnimationFrame(() =>
                resolve({ top: element.scrollTop, height: element.scrollHeight }),
              ),
            ),
          ),
      );
      afterNotch?.(state);
      stalled = state.top === top ? stalled + 1 : 0;
      top = state.top;
    }
  };
  await untilSettled(notchSize);
  await expect(scroller).toBeFocused();
  const samples = await observation.jsonValue();
  const visited = [...new Set(samples.flat())].sort((a, b) => a - b);
  expect(visited).toEqual(paths.map((_, index) => index + 1));
  const firstVisible = samples.flatMap((sample) => sample.slice(0, 1));
  for (let index = 1; index < firstVisible.length; index++) {
    expect(firstVisible[index]).toBeGreaterThanOrEqual(firstVisible[index - 1]!);
  }
  const settledHeight = await scroller.evaluate((element) => element.scrollHeight);
  await untilSettled(-notchSize, (state) => expect(state.height).toBe(settledHeight));
  await expect.poll(() => scroller.evaluate((element) => element.scrollTop)).toBe(0);
});
