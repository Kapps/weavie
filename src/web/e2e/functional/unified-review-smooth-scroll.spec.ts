import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";
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
  const editor = page.locator(".unified-review-file .monaco-editor");
  await expect(editor).toBeVisible();
  const mountedEditor = await editor.elementHandle();
  await editor.hover();
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
    expect(await mountedEditor!.evaluate((element) => element.isConnected)).toBe(true);
  }
});

test("wheel animation paints Monaco text at the current review position", async ({ page }) => {
  await page.locator(".editor-empty-review").click();
  const editor = page.locator(".unified-review-file .monaco-editor");
  await expect(editor).toBeVisible();
  await editor.hover();
  const observation = await page.evaluateHandle(() => {
    const monaco = (window as unknown as WeavieWindow).__WEAVIE_MONACO__!;
    const editor = monaco.editor.getEditors().find((candidate) => {
      const node = (candidate as EditorHandle).getDomNode();
      return (node as HTMLElement | null)?.closest(".unified-review-file");
    }) as EditorHandle & { getTopForLineNumber(lineNumber: number): number };
    const offsets: number[] = [];
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
        return offsets;
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
  const offsets = await observation.evaluate((sample) => sample.finish());
  expect(offsets.length).toBeGreaterThan(25);
  expect(
    Math.max(...offsets.map(Math.abs)),
    "painted text agrees with Monaco's current scroll geometry",
  ).toBeLessThanOrEqual(1);
});

test("section header dimensions are measured in the resize phase, not during mounting", async ({
  page,
}) => {
  const observation = await page.evaluateHandle(() => {
    const NativeObserver = window.ResizeObserver;
    const descriptor = Object.getOwnPropertyDescriptor(Element.prototype, "clientTop")!;
    let resizing = false;
    let synchronous = 0;
    let observed = 0;
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
    Object.defineProperty(Element.prototype, "clientTop", {
      ...descriptor,
      get() {
        if (this.classList.contains("unified-review-file")) {
          if (resizing) observed++;
          else synchronous++;
        }
        return descriptor.get!.call(this);
      },
    });
    return {
      finish: () => {
        window.ResizeObserver = NativeObserver;
        Object.defineProperty(Element.prototype, "clientTop", descriptor);
        return { synchronous, observed };
      },
    };
  });
  await page.locator(".editor-empty-review").click();
  await expect(page.locator(".unified-review-file .monaco-editor")).toBeVisible();
  const measurements = await observation.evaluate((sample) => sample.finish());
  expect(measurements.observed).toBeGreaterThan(0);
  expect(measurements.synchronous).toBe(0);
});

test("scrolling a remounted review preserves its diff paint", async ({ page }) => {
  await page.locator(".editor-empty-review").click();
  const section = page.locator(".unified-review-file");
  const editor = section.locator(".monaco-editor");
  await expect(editor).toBeVisible();
  const toggle = section.locator(".unified-review-file-toggle");
  await toggle.click();
  await expect(editor).toHaveCount(0);
  await toggle.click();
  await expect(editor).toBeVisible();
  const band = await section.locator(".weavie-inline-newfile").elementHandle();
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
