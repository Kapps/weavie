import { existsSync } from "node:fs";
import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import type { WeavieWindow } from "../harness/weavie-window";

test("a dynamically registered language provider serves the session model", async ({
  page,
  weavie,
}) => {
  await Promise.all([
    writeFile(join(weavie.workspace, "Widget.cs"), "public sealed class Widget {}\n"),
    writeFile(
      join(weavie.workspace, "Program.cs"),
      "public static class Program\n{\n    public static Widget Make() => new Widget();\n}\n",
    ),
  ]);

  await openFile(page, "Program.cs");
  await expect.poll(() => existsSync(join(weavie.workspace, ".fake-lsp-ready"))).toBe(true);
  await page.evaluate(() => {
    const editor = (window as WeavieWindow).__WEAVIE_EDITOR__;
    if (editor === undefined) {
      throw new Error("editor handle not available");
    }
    editor.focus();
    editor.setPosition({ lineNumber: 3, column: 20 });
  });
  await page.keyboard.press("F12");

  await expect(page.locator(".editor")).toHaveAttribute("data-active-file", /Widget\.cs$/);
});
