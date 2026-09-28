import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { awaitFontsSettled } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { awaitReviewSet } from "../harness/navigator";
import { appliedEdit } from "../harness/review";

const paths = Array.from(
  { length: 12 },
  (_, index) => `burst-${index.toString().padStart(2, "0")}.txt`,
);
const lines = Array.from({ length: 8 }, (_, index) => `Original line ${index}`);

test.use({
  workspaceSeed: {
    run: async (workspace) => {
      await Promise.all(paths.map((path) => writeFile(join(workspace, path), lines.join("\n"))));
    },
  },
  fakeScript: {
    steps: [
      { op: "mcp", tool: "setSetting", args: { key: "editor.smoothScrolling", value: false } },
      ...paths.flatMap((path) =>
        appliedEdit(
          path,
          lines.map((line, index) => (index === 3 ? `Changed ${path}` : line)).join("\n"),
        ),
      ),
    ],
  },
});

async function openWarmedReview(page: Page): Promise<void> {
  await awaitReviewSet(page, paths);
  await awaitFontsSettled(page);
  await page.locator(".editor-empty-review").click();
  const scrollbar = page.getByRole("scrollbar", { name: "Review scroll position" });
  for (const path of paths) {
    await scrollbar.press("Home");
    await page.locator(".unified-review-tree-row.file", { hasText: path }).click();
    const section = page.locator(".unified-review-file", {
      has: page.locator(".unified-review-file-name", { hasText: path }),
    });
    await expect(section.locator(".weavie-inline-removed-content")).toContainText(lines[3]!);
    await section.evaluate(
      (element) =>
        new Promise<void>((resolve) => {
          const observer = new ResizeObserver(() => {
            observer.disconnect();
            resolve();
          });
          observer.observe(element);
        }),
    );
  }
}

test("immediate wheel bursts commit once per frame across headers and embedded editors", async ({
  page,
}) => {
  await openWarmedReview(page);
  const scrollbar = page.getByRole("scrollbar", { name: "Review scroll position" });
  await page.clock.install();
  await page.clock.pauseAt(new Date((await page.evaluate(() => Date.now())) + 1_000));
  const driver = await scrollbar.evaluateHandle((bar) => {
    const scroller = bar.closest(".unified-review-diffs")!;
    const top = () => Number(bar.getAttribute("aria-valuenow"));
    const commits: number[] = [];
    const setAttribute = bar.setAttribute;
    bar.setAttribute = function (name, value) {
      const previous = top();
      setAttribute.call(this, name, value);
      if (name === "aria-valuenow" && top() !== previous) commits.push(top());
    };
    return {
      reset: (key: string) => {
        bar.dispatchEvent(new KeyboardEvent("keydown", { key, bubbles: true }));
      },
      clear: () => {
        commits.length = 0;
      },
      read: () => ({ top: top(), commits: [...commits] }),
      wheel: ({ selector, deltas }: { selector: string; deltas: number[] }) => {
        const bounds = scroller.getBoundingClientRect();
        const target = [...scroller.querySelectorAll(selector)].find((element) => {
          const rect = element.getBoundingClientRect();
          return rect.bottom > bounds.top && rect.top < bounds.bottom;
        });
        if (!target) throw new Error(`No visible wheel target: ${selector}`);
        for (const deltaY of deltas) {
          const event = new WheelEvent("wheel", {
            deltaY,
            deltaMode: WheelEvent.DOM_DELTA_PIXEL,
            bubbles: true,
            cancelable: true,
          });
          // Chromium's synthetic legacy deltas have the opposite sign to real wheel input.
          Object.defineProperties(event, {
            wheelDeltaY: { value: undefined },
            wheelDeltaX: { value: undefined },
            wheelDelta: { value: undefined },
          });
          target.dispatchEvent(event);
        }
      },
      painted: () => {
        const bounds = scroller.getBoundingClientRect();
        const visible = (element: Element) => {
          const rect = element.getBoundingClientRect();
          return rect.height > 0 && rect.bottom > bounds.top && rect.top < bounds.bottom;
        };
        return {
          indices: [...scroller.querySelectorAll<HTMLElement>(".unified-review-file")]
            .filter(visible)
            .map((section) => Number(section.dataset.index)),
          lines: [...scroller.querySelectorAll(".view-line")]
            .filter(visible)
            .map((line) => line.textContent)
            .filter(Boolean),
        };
      },
      dispose: () => {
        bar.setAttribute = setAttribute;
      },
    };
  });
  const sequences = [
    { key: "Home", deltas: Array.from({ length: 12 }, () => 17.25) },
    { key: "End", deltas: Array.from({ length: 12 }, () => -17.25) },
    { key: "Home", deltas: [100_000.25, -17.25, -21.5] },
    { key: "End", deltas: [-100_000.25, 17.25, 21.5] },
  ];
  for (const sequence of sequences) {
    await driver.evaluate((state, key) => state.reset(key), sequence.key);
    await page.clock.runFor(32);
    // The same events delivered separately are the normalization and per-event clamping oracle.
    for (const delta of sequence.deltas) {
      await driver.evaluate(
        (state, delta) =>
          state.wheel({
            selector: ".unified-review-file-header",
            deltas: [delta],
          }),
        delta,
      );
      await page.clock.runFor(32);
    }
    const expected = (await driver.evaluate((state) => state.read())).top;
    for (const selector of [".unified-review-file-header", ".monaco-editor .view-lines"]) {
      await driver.evaluate((state, key) => state.reset(key), sequence.key);
      await page.clock.runFor(32);
      const before = (await driver.evaluate((state) => state.read())).top;
      expect(expected).not.toBe(before);
      await driver.evaluate((state) => state.clear());
      await driver.evaluate((state, burst) => state.wheel(burst), {
        selector,
        deltas: sequence.deltas,
      });
      expect(
        await driver.evaluate((state) => state.read()),
        "input queues without synchronous commits",
      ).toEqual({ top: before, commits: [] });
      await page.clock.runFor(32);
      expect(
        await driver.evaluate((state) => state.read()),
        "one frame preserves the exact wheel destination",
      ).toEqual({ top: expected, commits: [expected] });
      const painted = await driver.evaluate((state) => state.painted());
      expect(painted.lines.length).toBeGreaterThan(0);
      expect(painted.indices.length).toBeGreaterThan(1);
      expect(painted.indices).toEqual([...painted.indices].sort((a, b) => a - b));
    }
  }
  await driver.evaluate((state) => state.dispose());
  await page.clock.resume();
});

test("review toolbar handoff preserves observed viewport dimensions across cached file boundaries", async ({
  page,
}, testInfo) => {
  await openWarmedReview(page);
  const scrollbar = page.getByRole("scrollbar", { name: "Review scroll position" });
  await scrollbar.press("Home");
  const observation = await page.evaluateHandle(async () => {
    const viewport = document.querySelector<HTMLElement>(".unified-review-diffs")!;
    const footer = document.querySelector<HTMLElement>(".unified-review-controls")!;
    const samples: {
      kind: string;
      height: number;
      controls: number;
      counter: string | null;
      top: string | null;
    }[] = [];
    let initial = 0;
    let ready!: () => void;
    const observed = new Promise<void>((resolve) => {
      ready = resolve;
    });
    const observer = new ResizeObserver((entries) => {
      for (const entry of entries) {
        samples.push({
          kind: entry.target === viewport ? "viewport" : "footer",
          height: entry.borderBoxSize[0]!.blockSize,
          controls: footer.childElementCount,
          counter: footer.querySelector(".weavie-inline-stack-sub")?.textContent ?? null,
          top: viewport.querySelector('[role="scrollbar"]')?.getAttribute("aria-valuenow") ?? null,
        });
      }
      initial += entries.length;
      if (initial >= 2) ready();
    });
    observer.observe(viewport, { box: "border-box" });
    observer.observe(footer, { box: "border-box" });
    await observed;
    return { read: () => samples, dispose: () => observer.disconnect() };
  });
  const initial = await observation.evaluate((state) => state.read());
  const heights = Object.fromEntries(initial.map((sample) => [sample.kind, sample.height]));
  expect(heights.footer).toBeGreaterThan(0);
  await scrollbar.press("End");
  await expect(
    page
      .locator(".unified-review-file", {
        has: page.locator(".unified-review-file-name", { hasText: paths.at(-1)! }),
      })
      .locator(".view-line")
      .first(),
  ).toBeVisible();
  for (const selector of [".unified-review-file-header", ".monaco-editor .view-lines"]) {
    await scrollbar.press("Home");
    for (let index = 0; index < 8; index++) {
      const point = await page.locator(".unified-review-diffs").evaluate((viewport, selector) => {
        const bounds = viewport.getBoundingClientRect();
        for (const element of viewport.querySelectorAll(selector)) {
          const rect = element.getBoundingClientRect();
          const top = Math.max(bounds.top, rect.top);
          const bottom = Math.min(bounds.bottom, rect.bottom);
          if (bottom - top > 2) return { x: rect.left + 20, y: (top + bottom) / 2 };
        }
        throw new Error(`No visible wheel target: ${selector}`);
      }, selector);
      await page.mouse.move(point.x, point.y);
      await page.mouse.wheel(0, 400);
      await page.evaluate(
        () =>
          new Promise<void>((resolve) => {
            requestAnimationFrame(() => requestAnimationFrame(() => resolve()));
          }),
      );
    }
  }
  const samples = await observation.evaluate((state) => state.read());
  await testInfo.attach("toolbar-viewport-observations", {
    body: JSON.stringify(samples, null, 2),
    contentType: "application/json",
  });
  expect(samples.filter((sample) => sample.height !== heights[sample.kind])).toEqual([]);
  await observation.evaluate((state) => state.dispose());
});
