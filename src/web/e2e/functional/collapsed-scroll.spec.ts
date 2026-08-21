import { openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import type { WeavieWindow } from "../harness/weavie-window";

// A container that is momentarily 0-height clamps Monaco's viewport to 5px, and a reveal against that clamp
// scrolls almost to the end of the file. That offset survives the container's recovery — `scrollBeyondLastLine`
// bounds scrollTop by content height, not viewport height — leaving a full-size editor parked past the last
// line with nothing but blank space rendered. This drives the collapse directly rather than waiting for the
// Windows CI timing that produced it six times (see docs/specs/e2e-flake-analysis.md).

test("a reveal against a collapsed viewport doesn't leave the editor scrolled past the file", async ({
  page,
}) => {
  await openFile(page, "hello.ts");
  await expect(page.locator(".view-line").first()).toBeVisible();

  const collapsed = await page.evaluate(async () => {
    const editor = (window as WeavieWindow).__WEAVIE_EDITOR__;
    const container = document.querySelector<HTMLElement>(".editor-surface .editor");
    if (editor === undefined || container === null) {
      throw new Error("editor not mounted");
    }
    const settle = () => new Promise((resolve) => requestAnimationFrame(resolve));
    const height = container.style.height;

    container.style.height = "0px";
    container.style.minHeight = "0px";
    container.style.flex = "0 0 0px";
    await settle();
    await settle();
    const clamped = editor.getLayoutInfo().height;
    const clientHeight = container.clientHeight;
    const clampedContent = editor.getContentHeight();
    // A reveal against a viewport this short can only scroll toward the end; asking for more than the
    // maximum lands exactly on it, which is the offset every CI occurrence captured.
    editor.setScrollTop(editor.getContentHeight());
    await settle();
    const scrolledWhileClamped = editor.getScrollTop();

    container.style.height = height;
    container.style.minHeight = "";
    container.style.flex = "";
    await settle();
    await settle();
    return {
      clamped,
      clientHeight,
      clampedContent,
      scrolledWhileClamped,
      contentHeight: editor.getContentHeight(),
      after: editor.getScrollTop(),
      viewport: editor.getLayoutInfo().height,
      renderedLines: document.querySelectorAll(".view-line").length,
    };
  });

  // The collapse and the scroll it causes are real: this is the state every occurrence captured.
  expect(collapsed.clientHeight).toBe(0);
  expect(collapsed.clamped).toBeLessThan(22);
  // Nothing bounds it but the clamped viewport, so it parks at that maximum — and on recovery Monaco
  // re-clamps it to the real one (content minus viewport), which is where every CI occurrence was found.
  expect(collapsed.scrolledWhileClamped).toBe(collapsed.clampedContent - collapsed.clamped);

  // Once the viewport is back, the file is at the top again and every line renders — not one blank line.
  expect(collapsed.viewport).toBeGreaterThan(22);
  expect(collapsed.after).toBe(0);
  expect(collapsed.renderedLines).toBeGreaterThan(1);
  await expect(page.locator(".view-line", { hasText: "export function greet" })).toBeVisible();
});
