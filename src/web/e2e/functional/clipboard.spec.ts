import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { clickIntoEditor, openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

async function copiedText(page: Page): Promise<string> {
  return page.evaluate(() => navigator.clipboard.readText());
}

test.use({ permissions: ["clipboard-read", "clipboard-write"] });

test("copy trims editor and input boundaries, preserving internal whitespace", async ({
  page,
  weavie,
}) => {
  const text = "  first  word\n    second\n\n";
  await writeFile(join(weavie.workspace, "notes.txt"), text);
  await openFile(page, "notes.txt");
  await clickIntoEditor(page);
  await page.keyboard.press("ControlOrMeta+a");
  await page.keyboard.press("ControlOrMeta+c");
  await expect.poll(() => copiedText(page)).toBe(text.trim());

  await page.evaluate(() => navigator.clipboard.writeText("before context-menu copy"));
  await page.locator(".monaco-editor .view-line").first().click({ button: "right" });
  await page.locator(".context-menu-item").filter({ hasText: /^Copy/ }).click();
  await expect.poll(() => copiedText(page)).toBe(text.trim());

  const input = page.locator(".tb-omnibar-input");
  for (const value of ["  input  words  ", "   "]) {
    await input.fill(value);
    await input.press("ControlOrMeta+a");
    await input.press("ControlOrMeta+c");
    await expect.poll(() => copiedText(page)).toBe(value.trim());
  }
});

test("copying rendered code trims the fence newline but preserves internal indentation", async ({
  page,
  weavie,
}) => {
  await writeFile(
    join(weavie.workspace, "README.md"),
    "```text\n  first  word\n    second\n\n```\n",
  );
  await openFile(page, "README.md");
  await page.locator(".editor-preview-toggle").click();
  const code = page.locator(".editor-preview-body pre code");
  await expect(code).toBeVisible();
  await code.evaluate((element) => {
    const range = document.createRange();
    range.selectNode(element);
    document.getSelection()?.removeAllRanges();
    document.getSelection()?.addRange(range);
  });
  await page.keyboard.press("ControlOrMeta+c");
  await expect.poll(() => copiedText(page)).toBe("first  word\n    second");
});

test("terminal selection copy and OSC 52 share the trimming policy", async ({ page }) => {
  await expect(page.locator('.terminal-surface[data-kind="terminal:claude"] .xterm')).toBeVisible();
  await page.evaluate(async () => {
    const terminal = Object.entries(window.__WEAVIE_TERMINALS__ ?? {}).find(([key]) =>
      key.endsWith(":claude"),
    )?.[1];
    if (!terminal) throw new Error("Agent terminal is unavailable");
    await new Promise<void>((resolve) => terminal.write("\r\n  terminal  words  ", resolve));
    terminal.select(0, terminal.buffer.active.baseY + terminal.buffer.active.cursorY, 19);
    terminal.focus();
  });
  await page.keyboard.press("Control+Shift+c");
  await expect.poll(() => copiedText(page)).toBe("terminal  words");
  await page.evaluate(() => {
    const terminal = Object.entries(window.__WEAVIE_TERMINALS__ ?? {}).find(([key]) =>
      key.endsWith(":claude"),
    )?.[1];
    terminal?.write(`\u001b]52;c;${btoa("  OSC  text\n\n")}\u0007`);
  });
  await expect.poll(() => copiedText(page)).toBe("OSC  text");
});
