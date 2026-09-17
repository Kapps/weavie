import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";
import { reviewScroll } from "../harness/review-scroll";

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
              ? // File 0 is excluded from the "wide" set: it's the one file whose first paint gates the
                // parked→active toolbar flip below (see that assertion's comment), so giving it the same
                // 1,000+ char lines as the wide files would add unnecessary diff-worker cost to exactly
                // the critical path that assertion's timeout has to cover. Shifted to `=== 1` to keep the
                // same wide-file count (10 of 30) and full scroll-order coverage.
                `export const value${lineIndex} = "changed ${index} ${lineIndex} ${"wide ".repeat(index % 3 === 1 ? 200 : 1)}";`
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
  // Flake (macOS only): 2026-09-16 06:01 UTC, run
  // https://github.com/Kapps/weavie/actions/runs/35061852210/job/104684242015 — this assertion hit the
  // 30s macOS/Windows default (config's per-platform `expect.timeout`) with the toolbar stuck on the
  // parked "press ↓ to start" text. Root cause: the parked→active toolbar flip is gated on file 1's
  // review editor actually painting (inline-diff.ts's `presentation.painted`), which needs Monaco's
  // shared editor-worker diff computation for a 7,000-line file (plus the virtualizer's overscanned
  // file 2 competing for the same worker) — real, one-time work that measured ~340ms locally but scaled
  // to ~4.7s under a synthetic 20x CPU slowdown (Emulation.setCPUThrottlingRate), well past linear for
  // this fixture's several 1,000+ char lines. This is the heaviest review fixture in the suite; a loaded
  // shared macOS/Windows runner can plausibly push that first paint past 30s. Widened only this assertion.
  await expect(page.locator(".weavie-inline-stack-sub")).toContainText(`file 1/${paths.length}`, {
    timeout: 60_000,
  });
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
  while ((await reviewScroll(page)).top < (await reviewScroll(page)).maximum) {
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
  const settledHeight = await reviewScroll(page).then(({ maximum }) => maximum);
  while ((await reviewScroll(page)).top > 0) {
    await page.mouse.wheel(0, -400);
    expect(await reviewScroll(page).then(({ maximum }) => maximum)).toBe(settledHeight);
  }
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
