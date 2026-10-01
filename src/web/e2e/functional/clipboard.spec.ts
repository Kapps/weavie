import { writeFile } from "node:fs/promises";
import { EOL } from "node:os";
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
  const expected = text.trim().replaceAll("\n", EOL);
  await writeFile(join(weavie.workspace, "notes.txt"), text);
  await openFile(page, "notes.txt");
  await clickIntoEditor(page);
  await page.keyboard.press("ControlOrMeta+a");
  await page.keyboard.press("ControlOrMeta+c");
  await expect.poll(() => copiedText(page)).toBe(expected);

  await page.evaluate(() => navigator.clipboard.writeText("before context-menu copy"));
  await page.locator(".monaco-editor .view-line").first().click({ button: "right" });
  await page.locator(".context-menu-item").filter({ hasText: /^Copy/ }).click();
  await expect.poll(() => copiedText(page)).toBe(expected);

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

test.describe("terminal clipboard", () => {
  const text = "  terminal  words  ";
  test.use({ fakeScript: { steps: [{ op: "print", text }] } });

  test("terminal selection copy and OSC 52 share the trimming policy", async ({ page }) => {
    const target = await page.waitForFunction((text) => {
      const terminal = Object.entries(window.__WEAVIE_TERMINALS__ ?? {}).find(([key]) =>
        key.endsWith(":claude"),
      )?.[1];
      if (!terminal) return null;
      const buffer = terminal.buffer.active;
      for (let row = 0; row < buffer.length; row++) {
        const column = buffer.getLine(row)!.translateToString().indexOf(text);
        if (column !== -1) return { terminal, row, column };
      }
      return null;
    }, text);
    await target.evaluate((target, length) => {
      const { terminal, row, column } = target!;
      terminal.select(column, row, length);
      terminal.focus();
    }, text.length);
    await page.keyboard.press("Control+Shift+c");
    await expect.poll(() => copiedText(page)).toBe("terminal  words");
    await target.evaluate((target) => {
      target!.terminal.write(`\u001b]52;c;${btoa("  OSC  text\n\n")}\u0007`);
    });
    await expect.poll(() => copiedText(page)).toBe("OSC  text");
    await target.dispose();
  });
});
