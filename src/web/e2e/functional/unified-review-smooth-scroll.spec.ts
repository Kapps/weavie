import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import type { editor as MonacoEditor } from "monaco-editor";
import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";
import { reviewEditor, reviewPaint } from "../harness/review-renderer";
import { reviewScroll } from "../harness/review-scroll";
import type { EditorHandle, WeavieWindow } from "../harness/weavie-window";

const content = Array.from({ length: 5_000 }, (_, index) => `new line ${index}`).join("\n");

test.use({
  fakeScript: {
    steps: [
      ...appliedEdit("smooth-review.txt", content),
      ...[false, true].flatMap((enabled) => [
        { op: "waitFile" as const, path: `{{WORKSPACE}}/toggle-${enabled}` },
        {
          op: "mcp" as const,
          tool: "setSetting",
          args: { key: "editor.smoothScrolling", value: enabled },
        },
        { op: "edit" as const, path: `{{WORKSPACE}}/applied-${enabled}`, content: "done" },
      ]),
    ],
  },
});

async function wheelFrames(page: Page): Promise<number[]> {
  await page.clock.pauseAt(new Date((await page.evaluate(() => Date.now())) + 1_000));
  const scrollbar = page.getByRole("scrollbar", { name: "Review scroll position" });
  const observation = await scrollbar.evaluateHandle((element) => {
    const positions: number[] = [Number(element.getAttribute("aria-valuenow"))];
    let frame = 0;
    const sample = () => {
      positions.push(Number(element.getAttribute("aria-valuenow")));
      frame = requestAnimationFrame(sample);
    };
    frame = requestAnimationFrame(sample);
    return {
      finish: () => {
        cancelAnimationFrame(frame);
        return positions;
      },
    };
  });
  await page.mouse.wheel(0, 120);
  await page.clock.runFor(350);
  const frames = await observation.evaluate((sample) => sample.finish());
  await page.clock.resume();
  return frames;
}

test("wheel animation follows the live smooth scrolling setting in an existing review", async ({
  page,
  weavie,
}) => {
  await page.clock.install();
  await page.locator(".editor-empty-review").click();
  const body = page.locator(".review-adaptive-body");
  await expect(reviewPaint(page)).toBeVisible();
  const mountedBody = await body.elementHandle();
  await reviewPaint(page).locator(".view-line").first().hover();
  const initial = await wheelFrames(page);
  expect(new Set(initial).size, "enabled wheel scrolling animates across frames").toBeGreaterThan(
    2,
  );

  for (const enabled of [false, true]) {
    await writeFile(join(weavie.workspace, `toggle-${enabled}`), "go");
    await expect
      .poll(() => readFile(join(weavie.workspace, `applied-${enabled}`), "utf8").catch(() => ""))
      .toBe("done");
    const frames = await wheelFrames(page);
    expect(frames.at(-1)).toBeGreaterThan(frames[0]!);
    if (enabled) expect(new Set(frames).size).toBeGreaterThan(2);
    else expect(new Set(frames).size).toBe(2);
    expect(await mountedBody!.evaluate((element) => element.isConnected)).toBe(true);
    await expect(reviewEditor(page)).toHaveCount(0);
  }
});

test.describe("steady unified scrolling", () => {
  test.use({
    fakeScript: {
      steps: ["a", "b", "c", "d", "e"].flatMap((name) => appliedEdit(`${name}.txt`, content)),
    },
  });

  test("wheel animation paints current text without resizing the sole live viewport", async ({
    page,
  }) => {
    await expect(page.locator(".editor-empty-review")).toContainText("5");
    await page.locator(".editor-empty-review").click();
    await page.locator(".unified-review-tree-row.file").first().click();
    const editor = reviewEditor(page);
    await expect(editor).toBeVisible();
    const scrollbar = page.getByRole("scrollbar", { name: "Review scroll position" });
    await scrollbar.press("PageDown");
    await scrollbar.press("PageDown");
    await editor.hover();
    const observation = await page.evaluateHandle(() => {
      const monaco = (window as unknown as WeavieWindow).__WEAVIE_MONACO__!;
      const editors = monaco.editor.getEditors().filter((candidate) => {
        const node = (candidate as EditorHandle).getDomNode();
        return (node as HTMLElement | null)?.closest(".unified-review-file");
      }) as MonacoEditor.IStandaloneCodeEditor[];
      const editor = editors[0]!;
      const offsets: number[] = [];
      const layouts: MonacoEditor.IDimension[] = [];
      const restore = editors.map((editor) => {
        const layout = editor.layout;
        editor.layout = function (...args) {
          if (args[0]) layouts.push(args[0]);
          return layout.apply(this, args);
        };
        return () => {
          editor.layout = layout;
        };
      });
      let frame = 0;
      const sample = () => {
        const node = editor.getDomNode() as HTMLElement;
        const line = node.querySelector<HTMLElement>(".view-line");
        const number = line?.textContent?.match(/new\sline\s(\d+)/)?.[1];
        if (line && number !== undefined) {
          const top = editor.getTopForLineNumber(Number(number) + 1) - editor.getScrollTop();
          offsets.push(line.getBoundingClientRect().top - node.getBoundingClientRect().top - top);
        }
        frame = requestAnimationFrame(sample);
      };
      frame = requestAnimationFrame(sample);
      return {
        finish: () => {
          cancelAnimationFrame(frame);
          for (const reset of restore) reset();
          return { offsets, layouts, editorCount: editors.length };
        },
      };
    });
    for (let index = 0; index < 25; index++) {
      await page.mouse.wheel(0, 120);
      await page.evaluate(
        () =>
          new Promise<void>((resolve) =>
            requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
          ),
      );
    }
    const { offsets, layouts, editorCount } = await observation.evaluate((sample) =>
      sample.finish(),
    );
    expect(editorCount, "only the activated file owns a live editor").toBe(1);
    await expect(page.locator(".review-adaptive-body")).toHaveCount(5);
    expect(layouts, "scrolling inside one file must not re-layout its editor").toEqual([]);
    expect(offsets.length).toBeGreaterThan(25);
    expect(
      Math.max(...offsets.map(Math.abs)),
      "painted text agrees with Monaco's current scroll geometry",
    ).toBeLessThanOrEqual(1);
  });
});

test("header positioning measures in the resize phase and scrolling reads no section geometry", async ({
  page,
}) => {
  const observation = await page.evaluateHandle(() => {
    const NativeObserver = window.ResizeObserver;
    const rect = Element.prototype.getBoundingClientRect;
    const computedStyle = window.getComputedStyle;
    let resizing = false;
    let synchronous = 0;
    let observed = 0;
    let scrolling = false;
    let scrollMeasurements = 0;
    window.ResizeObserver = new Proxy(NativeObserver, {
      construct(target, [callback]: [ResizeObserverCallback]) {
        return new target((entries, observer) => {
          resizing = true;
          try {
            callback(entries, observer);
          } finally {
            resizing = false;
          }
        });
      },
    });
    Element.prototype.getBoundingClientRect = function () {
      if (scrolling && this.matches(".unified-review-file, .unified-review-file-header"))
        scrollMeasurements++;
      return rect.call(this);
    };
    window.getComputedStyle = (element, pseudo) => {
      if (element.matches(".unified-review-file")) {
        if (resizing) observed++;
        else synchronous++;
        if (scrolling) scrollMeasurements++;
      }
      return computedStyle.call(window, element, pseudo);
    };
    return {
      observed: () => observed,
      beginScroll: () => {
        scrolling = true;
      },
      finish: () => {
        window.ResizeObserver = NativeObserver;
        Element.prototype.getBoundingClientRect = rect;
        window.getComputedStyle = computedStyle;
        return { synchronous, observed, scrollMeasurements };
      },
    };
  });
  await page.locator(".editor-empty-review").click();
  await expect(reviewPaint(page)).toBeVisible();
  await expect.poll(() => observation.evaluate((sample) => sample.observed())).toBeGreaterThan(0);
  await reviewPaint(page).locator(".view-line").first().hover();
  const before = (await reviewScroll(page)).top;
  await observation.evaluate((sample) => sample.beginScroll());
  for (let step = 0; step < 4; step++) {
    await page.mouse.wheel(0, 120);
    await page.evaluate(
      () =>
        new Promise<void>((resolve) =>
          requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
        ),
    );
  }
  await expect.poll(() => reviewScroll(page).then(({ top }) => top)).toBeGreaterThan(before);
  const measurements = await observation.evaluate((sample) => sample.finish());
  expect(measurements.observed).toBeGreaterThan(0);
  expect(measurements.synchronous).toBe(0);
  expect(measurements.scrollMeasurements).toBe(0);
});

test("scrolling a re-expanded review preserves its live diff paint", async ({ page }) => {
  await page.locator(".editor-empty-review").click();
  const section = page.locator(".unified-review-file");
  await page.locator(".unified-review-tree-row.file").click();
  const editor = reviewEditor(section);
  await expect(editor).toBeVisible();
  const original = await editor.elementHandle();
  const toggle = section.locator(".unified-review-file-toggle");
  await toggle.click();
  await expect(editor).toBeHidden();
  await expect(section.locator(".review-adaptive-body")).toBeHidden();
  await toggle.click();
  await reviewPaint(section)
    .locator(".view-line")
    .first()
    .click({ position: { x: 5, y: 5 } });
  await expect(editor).toBeVisible();
  expect(await original!.evaluate((node) => node.isConnected)).toBe(true);
  const band = await reviewPaint(section).locator(".weavie-inline-newfile").elementHandle();
  expect(band).not.toBeNull();
  await editor.hover();
  const before = (await reviewScroll(page)).top;
  for (let index = 0; index < 10; index++) {
    await page.mouse.wheel(0, 120);
    await page.evaluate(
      () => new Promise<void>((resolve) => requestAnimationFrame(() => resolve())),
    );
  }
  expect((await reviewScroll(page)).top).toBeGreaterThan(before);
  expect(await band!.evaluate((element) => element.isConnected)).toBe(true);
});
