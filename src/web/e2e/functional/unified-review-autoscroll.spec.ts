import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Locator, Page } from "@playwright/test";
import { pressDocumentStart } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { awaitReviewSet } from "../harness/navigator";
import { appliedEdit } from "../harness/review";
import { reviewScroll, scrollReview } from "../harness/review-scroll";

const paths = ["a.txt", "b.txt", "c.txt", "d.txt", "e.txt"];
const content = Array.from(
  { length: 60 },
  (_, index) => `line ${index} ${"wide ".repeat(100)}`,
).join("\n");

test.use({
  fakeScript: {
    steps: [
      ...paths.flatMap((path) => appliedEdit(path, content)),
      { op: "waitFile", path: "{{WORKSPACE}}/disable-autoscroll" },
      {
        op: "mcp",
        tool: "setSetting",
        args: { key: "editor.middleClickAutoscroll", value: false },
      },
      { op: "edit", path: "{{WORKSPACE}}/autoscroll-disabled", content: "done" },
    ],
  },
});

async function openReview(page: Page): Promise<void> {
  await awaitReviewSet(page, paths);
  await page.locator(".editor-empty-review").click();
  await page.locator(".unified-review-tree-row.file").first().click();
  await expect(page.locator(".unified-review-file .view-line").first()).toBeVisible();
}

async function arm(page: Page, target: Locator): Promise<{ x: number; y: number }> {
  const box = await target.boundingBox();
  if (box === null) throw new Error("Autoscroll target is not visible");
  const origin = { x: box.x + 100, y: box.y + Math.min(20, box.height / 2) };
  await page.mouse.click(origin.x, origin.y, { button: "middle" });
  await expect(page.locator(".middle-click-autoscroll-origin")).toBeVisible();
  return origin;
}

test("middle scrolling crosses files after its starting editor unmounts, without editing text", async ({
  page,
}) => {
  await openReview(page);
  const editor = page.locator(".unified-review-file .monaco-editor").first();
  const original = await editor.elementHandle();
  const observation = await page.evaluateHandle(() => {
    const monaco = (window as unknown as { __WEAVIE_MONACO__: typeof import("monaco-editor") })
      .__WEAVIE_MONACO__;
    const editor = monaco.editor
      .getEditors()
      .find((editor) => editor.getDomNode()?.closest(".unified-review-file"))!;
    editor.setSelection({ startLineNumber: 1, startColumn: 1, endLineNumber: 1, endColumn: 7 });
    const selection = editor.getSelection();
    const model = editor.getModel()!;
    const value = model.getValue();
    let disposed = false;
    editor.onDidDispose(() => {
      disposed = true;
    });
    return {
      scrollLeft: () => editor.getScrollLeft(),
      unchangedSelection: () => JSON.stringify(editor.getSelection()) === JSON.stringify(selection),
      unchangedText: () => model.getValue() === value,
      disposed: () => disposed,
    };
  });
  const origin = await arm(page, editor);
  await expect(editor).not.toHaveClass(/scroll-editor-on-middle-click-editor/);
  await page.mouse.move(origin.x + 120, origin.y);
  await expect.poll(() => observation.evaluate((sample) => sample.scrollLeft())).toBeGreaterThan(0);
  expect(await observation.evaluate((sample) => sample.unchangedSelection())).toBe(true);
  expect(await observation.evaluate((sample) => sample.unchangedText())).toBe(true);

  await page.mouse.move(origin.x + 120, origin.y + 250);
  await expect.poll(() => observation.evaluate((sample) => sample.disposed())).toBe(true);
  expect(await original!.evaluate((node) => node.isConnected)).toBe(false);
  await expect(page.locator(".middle-click-autoscroll-origin")).toBeVisible();
  await expect.poll(() => reviewScroll(page).then(({ top, maximum }) => maximum - top)).toBe(0);
  await expect(page.locator(".weavie-inline-stack-sub")).toContainText("file 5/5");
  await page.keyboard.press("Escape");
  await expect(page.locator(".middle-click-autoscroll-origin")).toHaveCount(0);
  await expect(page.locator(".middle-click-autoscrolling")).toHaveCount(0);
  expect(await observation.evaluate((sample) => sample.unchangedText())).toBe(true);

  const header = page.locator(".unified-review-file-header").last();
  const reverse = await arm(page, header);
  expect(reverse.y).toBeGreaterThan(100);
  await page.mouse.move(reverse.x, reverse.y - 100);
  await expect.poll(() => reviewScroll(page).then(({ top }) => top)).toBe(0);
  await page.keyboard.press("Escape");
});

for (const key of ["PageUp", "document start", "ArrowUp", "q"]) {
  test(`${key} stops review autoscroll and still reaches the editor`, async ({ page }) => {
    await openReview(page);
    const editor = page.locator(".unified-review-file .monaco-editor").first();
    const observation = await editor.evaluateHandle((node) => {
      const monaco = (window as unknown as { __WEAVIE_MONACO__: typeof import("monaco-editor") })
        .__WEAVIE_MONACO__;
      const editor = monaco.editor.getEditors().find((editor) => editor.getDomNode() === node)!;
      const model = editor.getModel()!;
      const original = model.getLineContent(20);
      editor.setPosition({ lineNumber: 20, column: 1 });
      editor.focus();
      return () => ({
        position: editor.getPosition(),
        text: model.getLineContent(20),
        original,
      });
    });
    const before = (await reviewScroll(page)).top;
    const origin = await arm(page, editor);
    await page.mouse.move(origin.x, origin.y + 40);
    await expect.poll(() => reviewScroll(page).then(({ top }) => top)).toBeGreaterThan(before);

    if (key === "document start") await pressDocumentStart(page);
    else await page.keyboard.press(key);
    await expect(page.locator(".middle-click-autoscroll-origin")).toHaveCount(0);
    await expect(page.locator(".middle-click-autoscrolling")).toHaveCount(0);
    const result = await observation.evaluate((sample) => sample());
    if (key === "q") {
      expect(result.text).toBe(`q${result.original}`);
      expect(result.position).toEqual({ lineNumber: 20, column: 2 });
    } else {
      expect(result.text).toBe(result.original);
      expect(result.position!.lineNumber).toBeLessThan(20);
    }
    const stopped = (await reviewScroll(page)).top;
    await page.evaluate(
      () =>
        new Promise<void>((resolve) =>
          requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
        ),
    );
    expect((await reviewScroll(page)).top).toBe(stopped);
  });
}

test("review autoscroll releases ownership on cancellation, tab teardown, and a live setting change", async ({
  page,
  weavie,
}) => {
  await openReview(page);
  const header = page.locator(".unified-review-file-header").first();
  const marker = page.locator(".middle-click-autoscroll-origin");
  await page
    .locator(".unified-review-file .margin")
    .first()
    .click({ button: "middle", position: { x: 5, y: 20 } });
  await expect(marker).toBeVisible();
  await page.keyboard.press("Escape");
  await arm(page, header);
  await page.mouse.wheel(0, 120);
  await expect(marker).toHaveCount(0);

  await scrollReview(page, "start");
  const gap = await header.boundingBox();
  expect(gap).not.toBeNull();
  await page.mouse.click(gap!.x + 100, gap!.y - 10, { button: "middle" });
  await expect(marker).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(marker).toHaveCount(0);
  await arm(page, header);
  await page.evaluate(() => window.dispatchEvent(new Event("blur")));
  await expect(marker).toHaveCount(0);

  const origin = await arm(page, header);
  await page.mouse.click(origin.x, origin.y);
  await expect(marker).toHaveCount(0);
  await expect(page.locator(".unified-review-file .monaco-editor").first()).toBeVisible();

  await arm(page, header);
  await page.keyboard.press("ControlOrMeta+w");
  await expect(page.locator(".unified-review")).toHaveCount(0);
  await expect(marker).toHaveCount(0);
  await openReview(page);

  await arm(page, header);
  await writeFile(join(weavie.workspace, "disable-autoscroll"), "go");
  await expect
    .poll(() => readFile(join(weavie.workspace, "autoscroll-disabled"), "utf8").catch(() => ""))
    .toBe("done");
  await expect(marker).toHaveCount(0);
  await header.click({ button: "middle" });
  await expect(marker).toHaveCount(0);
  const editor = page.locator(".unified-review-file .monaco-editor").first();
  const delivered = await editor.evaluateHandle((node) => {
    let pressed = false;
    node.addEventListener(
      "mousedown",
      () => {
        pressed = true;
      },
      { capture: true, once: true },
    );
    return () => pressed;
  });
  await editor.click({ button: "middle", position: { x: 100, y: 20 } });
  expect(await delivered.evaluate((pressed) => pressed())).toBe(true);
  await expect(marker).toHaveCount(0);
});
