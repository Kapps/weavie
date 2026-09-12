import { mkdir, writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Locator, Page } from "@playwright/test";
import { awaitEditorReady, clickIntoEditor, openFile, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

const panel = (page: Page, kind: "files" | "search"): Locator =>
  page.locator(`.tool-panel[data-tool="${kind}"]`);

async function bounds(locator: Locator): Promise<{
  x: number;
  y: number;
  width: number;
  height: number;
}> {
  const box = await locator.boundingBox();
  if (box === null) throw new Error("Expected a visible pane");
  return box;
}

async function expectFloatingWithinLayout(tool: Locator): Promise<void> {
  await expect(tool).toHaveClass(/tool-floating/);
  const [floating, owner] = await Promise.all([
    bounds(tool),
    bounds(tool.page().locator(".layout-root")),
  ]);
  expect(floating.x).toBeGreaterThanOrEqual(owner.x);
  expect(floating.y).toBeGreaterThanOrEqual(owner.y);
  expect(floating.x + floating.width).toBeLessThanOrEqual(owner.x + owner.width);
  expect(floating.y + floating.height).toBeLessThanOrEqual(owner.y + owner.height);
}

test("Escape dismisses only the top floating tool after nested UI cancels", async ({ page }) => {
  await openFile(page, "hello.ts");
  const viewMenu = page.getByRole("menuitem", { name: "View", exact: true });
  await viewMenu.click();
  await expect(page.getByRole("menu")).toBeVisible();
  await expect(viewMenu).not.toBeFocused();
  await page.keyboard.press("Escape");
  await expect(page.getByRole("menu")).toBeHidden();
  await expect(viewMenu).toBeFocused();
  await page.keyboard.press("ControlOrMeta+b");
  const files = panel(page, "files");
  const search = panel(page, "search");
  await expectFloatingWithinLayout(files);
  await files.locator(".browser-row", { hasText: "hello.ts" }).click();
  await expect(page.locator(".editor")).toHaveAttribute("data-active-file", /hello\.ts$/);
  await expect(page.getByRole("textbox", { name: "Editor content", exact: true })).toBeFocused();
  await page.keyboard.press("Escape");
  await expect(files).toBeHidden();

  await page.keyboard.press("ControlOrMeta+b");
  await expect(files).toBeVisible();
  await page.keyboard.press("ControlOrMeta+Shift+f");
  await expectFloatingWithinLayout(search);
  await search.locator(".search-input").fill("greet");
  await expect(search.locator(".search-row")).toHaveCount(2);
  await page.keyboard.press("Enter");
  await expect(search.locator(".search-input")).not.toBeFocused();

  await page.locator(".editor-tab", { hasText: "hello.ts" }).click({ button: "right" });
  await expect(page.locator(".context-menu")).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(page.locator(".context-menu")).toHaveCount(0);
  await expect(search).toBeVisible();
  await expect(files).toBeVisible();

  await page.locator(".editor-tab", { hasText: "hello.ts" }).click({ button: "right" });
  await expect(page.locator(".context-menu")).toBeVisible();
  await page.keyboard.press("ControlOrMeta+Shift+f");
  await expect(page.locator(".context-menu")).toHaveCount(0);
  await expect(search.locator(".search-input")).toBeFocused();
  await page.keyboard.press("Escape");
  await expect(search).toBeHidden();
  await expect(files).toBeVisible();
  await page.keyboard.press("ControlOrMeta+Shift+f");
  await expect(search).toBeVisible();

  await page.keyboard.press("ControlOrMeta+p");
  await expect(page.locator(".tb-omnibar-box")).toHaveClass(/\bopen\b/);
  await page.keyboard.press("Escape");
  await expect(page.locator(".tb-omnibar-box")).not.toHaveClass(/\bopen\b/);
  await expect(search).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(search).toBeHidden();
  await expect(files).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(files).toBeHidden();
});

test("docking preserves live panes and query, with stacked tools and remembered sizes", async ({
  page,
  weavie,
}) => {
  test.slow();
  await mkdir(join(weavie.workspace, "folder"));
  await writeFile(join(weavie.workspace, "folder", "child.txt"), "Keep this directory expanded\n");
  await openFile(page, "hello.ts");
  await expect(page.locator(".terminal-surface .xterm")).toHaveCount(2);
  const originalPanes = await page
    .locator(".layout-root")
    .evaluateHandle((root) => [
      ...root.querySelectorAll(".terminal-surface .xterm, .editor-surface .monaco-editor"),
    ]);
  const files = panel(page, "files");
  const search = panel(page, "search");
  await page.keyboard.press("ControlOrMeta+Shift+f");
  await search.locator(".search-input").fill("greet");
  await expect(search.locator(".search-row")).toHaveCount(2);
  const input = await search.locator(".search-input").elementHandle();
  await search.getByRole("button", { name: /^Stay Open/ }).click();
  await expect(search).not.toHaveClass(/tool-floating/);
  await page.keyboard.press("ControlOrMeta+b");
  await files.locator(".browser-row", { hasText: "folder" }).click();
  await expect(files.locator(".browser-row", { hasText: "child.txt" })).toBeVisible();
  await files.getByRole("button", { name: /^Stay Open/ }).click();
  await expect(files).not.toHaveClass(/tool-floating/);
  await expect(files.locator(".browser-row", { hasText: "child.txt" })).toBeVisible();
  await expect(search.locator(".search-input")).toHaveValue("greet");
  expect(
    await search
      .locator(".search-input")
      .evaluate((current, previous) => current === previous, input),
  ).toBe(true);
  expect(
    await originalPanes.evaluate(
      (panes) => panes.length === 3 && panes.every((pane) => pane.isConnected),
    ),
  ).toBe(true);

  const filesBox = await bounds(files);
  const searchBox = await bounds(search);
  const editorBox = await bounds(page.locator(".editor-surface"));
  expect(filesBox.x).toBeCloseTo(searchBox.x, 0);
  expect(filesBox.width).toBeCloseTo(searchBox.width, 0);
  expect(filesBox.y + filesBox.height).toBeCloseTo(searchBox.y, 0);
  expect(filesBox.x + filesBox.width).toBeLessThanOrEqual(editorBox.x);
  await page.keyboard.press("Escape");
  await expect(files).toBeVisible();
  await expect(search).toBeVisible();
  await page.keyboard.press("Control+1");
  await expect(page.locator(".agent-terminal-surface")).toHaveClass(/\bactive\b/);
  await page.keyboard.press("Control+2");
  await expect(page.locator('.terminal-surface[data-kind="terminal:shell"]')).toHaveClass(
    /\bactive\b/,
  );
  await page.keyboard.press("Control+3");
  await expect(page.locator(".editor-surface")).toHaveClass(/\bactive\b/);

  const divider = page.locator(".split-handle.vertical").first();
  const dividerBox = await bounds(divider);
  await page.mouse.move(dividerBox.x + dividerBox.width / 2, dividerBox.y + dividerBox.height / 4);
  await page.mouse.down();
  await page.mouse.move(dividerBox.x + 80, dividerBox.y + dividerBox.height / 4, { steps: 6 });
  await expect.poll(async () => (await bounds(files)).width).toBeGreaterThan(filesBox.width + 60);
  await page.mouse.up();
  await expect.poll(async () => (await bounds(files)).width).toBeGreaterThan(filesBox.width + 60);
  const resized = await bounds(files);
  await files.getByRole("button", { name: /^Close/ }).click();
  await expect(files).toBeHidden();
  await page.keyboard.press("ControlOrMeta+b");
  await expect(files).toBeVisible();
  expect((await bounds(files)).width).toBeCloseTo(resized.width, 0);
  expect((await bounds(files)).height).toBeCloseTo(resized.height, 0);

  await runCommand(page, "Close Tool Panel");
  await expect(files).toBeHidden();
  await expect(search).toBeVisible();
  await page.keyboard.press("ControlOrMeta+b");
  await expect(files).toBeVisible();

  await search.getByRole("button", { name: /^Float/ }).click();
  await expectFloatingWithinLayout(search);
  await expect(search.locator(".search-input")).toHaveValue("greet");
  await search.getByRole("button", { name: /^Stay Open/ }).click();
  await expect(search).not.toHaveClass(/tool-floating/);
  await page.reload();
  await awaitEditorReady(page);
  await expect(files).toBeVisible();
  await expect(search).toBeVisible();
  await expect(files).not.toHaveClass(/tool-floating/);
  await expect(search).not.toHaveClass(/tool-floating/);
  expect((await bounds(files)).width).toBeCloseTo(resized.width, 0);
});

test("compact overlays and fullscreen retain the desktop dock", async ({ page }) => {
  await openFile(page, "hello.ts");
  const files = panel(page, "files");
  await page.keyboard.press("ControlOrMeta+b");
  await files.getByRole("button", { name: /^Stay Open/ }).click();
  await expect(files).not.toHaveClass(/tool-floating/);
  await clickIntoEditor(page);
  await page.keyboard.press("Alt+Shift+Enter");
  await expect(files).toBeHidden();
  await page.keyboard.press("ControlOrMeta+b");
  await expect(files).toBeVisible();
  await expect(page.locator(".editor-surface")).toBeVisible();

  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.locator(".app")).toHaveClass(/compact/);
  await expectFloatingWithinLayout(files);
  await page.keyboard.press("Escape");
  await expect(files).toBeHidden();
  await page.getByRole("button", { name: "Sessions", exact: true }).click();
  await expect(page.locator(".session-inbox")).toBeVisible();
  await page.keyboard.press("ControlOrMeta+b");
  await expect(files).toBeVisible();
  await expectFloatingWithinLayout(files);
  await page.keyboard.press("Escape");
  await expect(files).toBeHidden();
  await page.setViewportSize({ width: 1280, height: 800 });
  await expect(files).toBeVisible();
  await expect(files).not.toHaveClass(/tool-floating/);
});
