import { existsSync } from "node:fs";
import { mkdir, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { openFile, runCommand } from "../harness/actions";
import { writeTestProgramWrapper } from "../harness/fake-claude";
import { expect, test } from "../harness/fixtures";
import { fakeAcpProgram } from "../harness/test-programs";
import type { WeavieWindow } from "../harness/weavie-window";

test.use({
  fakeScript: {
    steps: [
      { op: "waitFile", path: "{{WORKSPACE}}/configure" },
      {
        op: "mcp",
        tool: "setSetting",
        args: { key: "lsp.csharp.command", value: ["tools/configured-ls"] },
      },
      { op: "edit", path: "{{WORKSPACE}}/configured", content: "" },
    ],
  },
});

test("a configured language server replaces the one on PATH once servers restart", async ({
  page,
  weavie,
}) => {
  const ready = (name: string) => existsSync(join(weavie.workspace, name));
  await Promise.all([
    writeFile(join(weavie.workspace, "Widget.cs"), "public sealed class Widget {}\n"),
    writeFile(
      join(weavie.workspace, "Program.cs"),
      "public static class Program\n{\n    public static Widget Make() => new Widget();\n}\n",
    ),
  ]);
  await openFile(page, "Program.cs");
  await expect.poll(() => ready(".fake-lsp-ready")).toBe(true);

  await writeFile(join(weavie.workspace, "configure"), "");
  await expect.poll(() => ready("configured")).toBe(true);
  await runCommand(page, "Restart Language Servers");
  await expect(
    page.locator(".toast", { hasText: /C# language server not found \(tried .*configured-ls\)/ }),
  ).toBeVisible();

  const tools = join(weavie.workspace, "tools");
  await mkdir(tools);
  await writeTestProgramWrapper(
    tools,
    process.platform === "win32" ? "configured-ls.cmd" : "configured-ls",
    fakeAcpProgram,
    ["lsp", ".configured-lsp-ready"],
  );
  await runCommand(page, "Restart Language Servers");
  await expect.poll(() => ready(".configured-lsp-ready")).toBe(true);

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
