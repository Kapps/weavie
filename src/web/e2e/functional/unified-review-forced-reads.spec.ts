import { expect, test } from "../harness/fixtures";
import { awaitReviewSet } from "../harness/navigator";
import { appliedEdit } from "../harness/review";

// A geometry read while layout is dirty forces a synchronous layout, and mid-scroll that flushes the frame's
// accumulated style invalidation — one dropped frame per file boundary on the native build. The review owns the
// numbers these reads ask for (scroll offsets, viewport height), so scrolling must not measure the DOM per frame.
const FILES = 12;
const LINES = 120;
const STRIDE = 30;
const base = Array.from({ length: LINES }, (_, index) => `export const value${index} = ${index};`);
const edited = base
  .flatMap((line, index) =>
    index % STRIDE === 0
      ? [`export const value${index} = ${index} + 1;`, `export const added${index} = 0;`]
      : [line],
  )
  .filter((_, index) => index % STRIDE > 6 || index % STRIDE === 0)
  .join("\n");
const names = Array.from({ length: FILES }, (_, index) => `read${index}.ts`);

test.describe("Review Changes tab — geometry reads while scrolling", () => {
  test.use({
    fakeScript: {
      steps: names.flatMap((name) => [
        { op: "edit" as const, path: `{{WORKSPACE}}/${name}`, content: base.join("\n") },
        ...appliedEdit(name, edited),
      ]),
    },
  });

  test("scrolling the review does not force a geometry read every frame", async ({ page }) => {
    await awaitReviewSet(page, names);
    await page.locator(".editor-empty-review").click();
    const scroller = page.locator(".unified-review-diffs");
    await expect(scroller).toBeVisible();
    await expect(page.locator(".unified-review-file .monaco-editor").first()).toBeVisible();

    // Counts only reads whose stack reaches Weavie's own bundles; Monaco measures its own text and the harness
    // walks the DOM to resolve locators, and neither is this assertion's business.
    await page.evaluate(() => {
      const counter = window as unknown as { __appReads: number; __frames: number };
      counter.__appReads = 0;
      counter.__frames = 0;
      const note = (): void => {
        const frames = (new Error().stack ?? "").split("\n").slice(2, 8);
        if (frames.some((frame) => /\/assets\/(?!monaco-)/.test(frame))) counter.__appReads += 1;
      };
      const rect = Element.prototype.getBoundingClientRect;
      Element.prototype.getBoundingClientRect = function (...args) {
        note();
        return rect.apply(this, args);
      };
      for (const name of [
        "clientHeight",
        "clientWidth",
        "clientTop",
        "offsetHeight",
        "offsetTop",
      ]) {
        const proto = name.startsWith("offset") ? HTMLElement.prototype : Element.prototype;
        const descriptor = Object.getOwnPropertyDescriptor(proto, name);
        if (descriptor?.get === undefined) continue;
        Object.defineProperty(proto, name, {
          ...descriptor,
          get() {
            note();
            return descriptor.get!.call(this);
          },
        });
      }
      const tick = (): void => {
        counter.__frames += 1;
        requestAnimationFrame(tick);
      };
      requestAnimationFrame(tick);
    });

    const bounds = (await scroller.boundingBox())!;
    await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
    for (let notch = 0; notch < 160; notch++) {
      await page.mouse.wheel(0, 120);
      await page.evaluate(
        () => new Promise((resolve) => requestAnimationFrame(() => resolve(null))),
      );
    }

    const { appReads, frames } = await page.evaluate(() => {
      const counter = window as unknown as { __appReads: number; __frames: number };
      return { appReads: counter.__appReads, frames: counter.__frames };
    });
    expect(frames, "the scroll must actually animate").toBeGreaterThan(40);
    // Averaging under one read per frame proves no read sits on the per-frame path: measured 2.58 per frame when
    // the visible-file lookup measured the DOM, against 0.59 once it took the height from the scroll owner.
    expect(appReads).toBeLessThan(frames);
  });
});
