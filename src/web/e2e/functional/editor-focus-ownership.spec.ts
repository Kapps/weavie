import { readFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { openCommandPalette, openFile, pressDocumentEnd, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { type HeldResponse, holdHostResponse } from "../harness/held-response";
import { appliedEdit } from "../harness/review";
import { reviewPaint } from "../harness/review-renderer";
import { reviewScroll } from "../harness/review-scroll";

const content =
  "export function greet(name: string): string {\n" +
  "  return `Review kept this greeting, ${name}!`;\n" +
  "}\n\n" +
  'const message = greet("weavie");\n' +
  "console.warn(message);\n";
const replies = new WeakMap<Page, HeldResponse>();

test.use({
  fakeScript: { steps: appliedEdit("hello.ts", content) },
  preNavigate: {
    run: async (page) => {
      replies.set(page, await holdHostResponse(page));
    },
  },
});

async function openPalette(page: Page, query: string): Promise<void> {
  await openCommandPalette(page);
  await page.locator(".tb-omnibar-input").fill(query);
  await expectPalette(page, query);
}

async function expectPalette(page: Page, query: string): Promise<void> {
  await expect(page.locator(".tb-omnibar-input")).toBeFocused();
  await expect(page.locator(".tb-omnibar-input")).toHaveValue(query);
  await expect(page.locator(".tb-omnibar-box")).toHaveClass(/\bopen\b/);
}

async function readingState(page: Page): Promise<unknown> {
  return page.evaluate(() => {
    const monaco = (window as unknown as { __WEAVIE_MONACO__: typeof import("monaco-editor") })
      .__WEAVIE_MONACO__;
    return monaco.editor.getEditors().map((editor) => ({
      model: editor.getModel()?.uri.toString(),
      selections: editor.getSelections(),
      top: editor.getScrollTop(),
      left: editor.getScrollLeft(),
    }));
  });
}

for (const surface of ["file", "unified review"] as const) {
  test(`a delayed ${surface} Undo reveal cannot take focus from a newer palette`, async ({
    page,
    weavie,
  }) => {
    await openFile(page, "hello.ts");
    if (surface === "unified review") await page.locator(".editor-review-open").click();
    const paint = surface === "file" ? page.locator(".editor-surface") : reviewPaint(page);
    await expect(paint.locator(".weavie-inline-pending-keep")).toHaveCount(2);
    await paint.locator(".weavie-inline-pending-keep").first().click();
    await expect(paint.locator(".weavie-inline-accepted")).toHaveCount(1);
    await paint.locator(".weavie-inline-pending-revert").click();
    const path = join(weavie.workspace, "hello.ts");
    await expect
      .poll(() => readFile(path, "utf8"))
      .toBe(content.replace("console.warn", "console.log"));

    const reply = replies.get(page)!;
    reply.hold((message) => message.feature === "review" && message.name === "undo");
    await runCommand(page, "Undo Revert (Review)");
    await expect.poll(() => readFile(path, "utf8")).toBe(content);
    await expect.poll(() => reply.received()?.payload).toEqual({ path, line: 6 });
    await openPalette(page, ">Redo Review Action");
    const redo = page.locator(".tb-omnibar-row").filter({
      has: page.locator(".tb-row-leaf", { hasText: /^Redo Review Action$/ }),
    });
    await expect(redo).toBeVisible();
    await expect(paint.locator(".weavie-inline-added")).toHaveCount(1);
    const reading = await readingState(page);
    const outer = surface === "unified review" ? await reviewScroll(page) : undefined;
    await reply.release();
    expect(await readingState(page)).toEqual(reading);
    if (outer !== undefined) expect(await reviewScroll(page)).toEqual(outer);
    await expectPalette(page, ">Redo Review Action");
    await redo.click();
    await expect(page.locator(".tb-omnibar-dropdown")).toHaveCount(0);
    await expect
      .poll(() => readFile(path, "utf8"))
      .toBe(content.replace("console.warn", "console.log"));
    if (surface === "unified review") {
      const section = page.locator(".unified-review-file", {
        has: page.locator(".unified-review-file-name", { hasText: "hello.ts" }),
      });
      await expect(section.locator(".unified-review-status")).toHaveText("Reviewed");
      const disclosure = section.locator(".unified-review-file-toggle");
      await expect(disclosure).toHaveAttribute("aria-expanded", "false");
      await disclosure.click();
      await expect(section.locator(".unified-review-rejection pre")).toHaveText(
        "console.warn(message);",
      );
    }
    await expect(paint.locator(".weavie-inline-added")).toHaveCount(0);
    await expect(paint.locator(".weavie-inline-accepted")).toHaveCount(1);
  });
}

test("a held file acquisition completes its placement without taking a newer palette's focus", async ({
  page,
  weavie,
}) => {
  await openFile(page, "hello.ts");
  await pressDocumentEnd(page);
  await expect
    .poll(() => page.evaluate(() => window.__WEAVIE_EDITOR__?.getPosition()?.lineNumber))
    .toBe(7);
  const reply = replies.get(page)!;
  reply.hold(
    (message) =>
      message.feature === "files" &&
      message.name === "read" &&
      (message.payload as { path: string }).path.endsWith("notes.txt"),
  );
  const input = page.locator(".tb-omnibar-input");
  await input.click();
  await input.fill("notes.txt");
  await expect(page.locator(".tb-omnibar-row", { hasText: "notes.txt" })).toBeVisible();
  await input.press("Enter");
  await expect(page.locator(".editor-tab.active")).toContainText("notes.txt");
  await expect.poll(() => reply.received() !== undefined).toBe(true);
  await expect(page.locator(".editor")).toHaveAttribute(
    "data-active-file",
    join(weavie.workspace, "hello.ts"),
  );
  await openPalette(page, ">Go Back");
  await reply.release();
  await expect(page.locator(".editor")).toHaveAttribute(
    "data-active-file",
    join(weavie.workspace, "notes.txt"),
  );
  await expect
    .poll(() => page.evaluate(() => window.__WEAVIE_EDITOR__?.getPosition()))
    .toEqual({ lineNumber: 1, column: 1 });
  expect(await page.evaluate(() => window.__WEAVIE_EDITOR__?.getModel()?.getValue())).toBe(
    await readFile(join(weavie.workspace, "notes.txt"), "utf8"),
  );
  await expectPalette(page, ">Go Back");
});
