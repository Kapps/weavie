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
  weavie,
}) => {
  test.slow();
  await expect(page.locator(".editor-empty-review")).toContainText(`${paths.length}`);
  await page.reload();
  await expect(page.locator(".editor-empty-review")).toContainText(`${paths.length}`);
  await page.locator(".editor-empty-review").click();
  const scroller = page.locator(".unified-review-diffs");
  await expect(scroller).toBeVisible();
  await expect(page.locator(".weavie-inline-stack-sub")).toContainText(
    `${paths.length} files · press ↓ to start`,
  );
  await page.locator(".unified-review-tree-row.file").first().click();
  await expect(page.locator(".weavie-inline-stack-sub")).toContainText(`file 1/${paths.length}`);
  const firstEditor = page.locator(".unified-review-file .monaco-editor").first();
  // Flake: 2026-09-28 ~06:10 UTC, run
  // https://github.com/Kapps/weavie/actions/runs/36383469427/job/108804715135 — this exact click hit
  // "Test timeout of 180000ms exceeded" on the release e2e's Windows leg, with Playwright's retry log
  // showing the click target repeatedly judged unstable / intercepted by plain `<html>` background for
  // the full 180s. Investigated: reproduced this fixture (30 files, wide word-wrapped lines) locally
  // under CPU throttling up to 20x (Emulation.setCPUThrottlingRate) to simulate a slow/contended
  // runner — across two throttled runs the click target's bounding box was pixel-identical on every
  // poll from selection through click, and the click itself always resolved cleanly. Could not
  // reproduce the instability by any means available here. This matches the class of editor-mount
  // geometry instability that draft PR #936 ("Stabilize unified review geometry and scroll ownership")
  // already owns; no test-level wait would fix a real virtualization race without just disguising it as
  // a longer timeout, so no change was made to this click.
  await firstEditor
    .locator(".view-line")
    .first()
    .click({ position: { x: 4, y: 4 } });
  await expect(firstEditor).toHaveClass(/focused/);
  // Records file-viewport occupancy and the scrollbar's max extent on every DOM mutation, not just
  // ones a Playwright-side poll happens to land on — see the wheel loops below for why that matters.
  const observation = await scroller.evaluateHandle((element) => {
    const samples: number[][] = [];
    const heights: number[] = [];
    const sample = (mutations: MutationRecord[]) => {
      for (const mutation of mutations) {
        if (mutation.attributeName === "aria-valuemax") {
          heights.push(Number((mutation.target as HTMLElement).getAttribute("aria-valuemax")));
        }
      }
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
      attributeFilter: ["aria-valuenow", "aria-valuemax"],
    });
    sample([]);
    return { samples, heights };
  });
  await scroller.hover();
  await page.keyboard.down("Alt");
  const scrollbar = await page
    .getByRole("scrollbar", { name: "Review scroll position" })
    .elementHandle();
  if (scrollbar === null) throw new Error("Review scrollbar is missing");
  const position = () => scrollbar.evaluate(readReviewScroll);
  // Flake: 2026-09-28 ~06:10 UTC, run
  // https://github.com/Kapps/weavie/actions/runs/36383469427/job/108804715135 — a sibling test in the
  // same shard timed out with the full 180s budget spent on this loop (see stale PR #903, which
  // diagnosed the same round-trip cost against the pre-Alt/scrollbar version of this test). Re-measured
  // fresh against today's code: each wheel notch is still a real CDP round trip, and this fixture's
  // >70,000px scroll area took ~285 of them (~72s combined with the reverse pass, on an idle runner) —
  // comfortably over the 90s Linux "slow" budget on its own, worse under any contention. Widening the
  // notch (400px → 1200px, matching #903's technique) measured to make ZERO difference: Monaco/vscode's
  // wheel handling normalizes a notch to a fixed step once Alt-accelerated, independent of the raw
  // delta — so unlike #903's fixture, this one can't be sped up by moving further per notch. The actual
  // lever is round-trip count: checking `position()` after every notch was never load-bearing (the
  // file-visit and geometry assertions below already read a continuous in-page observer, not this
  // poll), so this only polls every few notches to decide when to stop. Measured ~56-58s combined,
  // 3/3 local runs green.
  const CHECK_EVERY = 5;
  let scroll = await position();
  while (scroll.top < scroll.maximum) {
    for (let i = 0; i < CHECK_EVERY && scroll.top < scroll.maximum; i++) {
      await page.mouse.wheel(0, 400);
    }
    scroll = await position();
  }
  await expect(scroller).toBeFocused();
  const { samples } = await observation.evaluate((state) => state);
  const visited = [...new Set(samples.flat())].sort((a, b) => a - b);
  expect(visited).toEqual(paths.map((_, index) => index + 1));
  const firstVisible = samples.flatMap((sample) => sample.slice(0, 1));
  expect(firstVisible).toEqual([...firstVisible].sort((a, b) => a - b));
  const settledHeight = scroll.maximum;
  const heightsBeforeReverse = await observation.evaluate((state) => state.heights.length);
  while (scroll.top > 0) {
    for (let i = 0; i < CHECK_EVERY && scroll.top > 0; i++) {
      await page.mouse.wheel(0, -400);
    }
    scroll = await position();
  }
  const reverseHeights = await observation.evaluate(
    (state, from) => [...new Set(state.heights.slice(from))],
    heightsBeforeReverse,
  );
  expect(reverseHeights).toEqual([settledHeight]);
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
  expect(weavie.log()).not.toContain("dropped a page connection");
});
