import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { openFile, pressDocumentEnd } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Sticky scroll re-requests the whole file's symbol tree after every pause in typing, so it is off past
// editor.stickyScrollMaxLines — decided per file, and re-decided as edits cross the limit.
const lines = (count: number) =>
  `${Array.from({ length: count }, (_, i) => `export const v${i} = ${i};`).join("\n")}\n`;

test.use({
  workspaceSeed: {
    run: async (workspace) => {
      await writeFile(join(workspace, "short.ts"), lines(10));
      await writeFile(join(workspace, "long.ts"), lines(30));
    },
  },
  fakeScript: {
    steps: [
      { op: "mcp", tool: "setSetting", args: { key: "editor.stickyScrollMaxLines", value: 20 } },
    ],
  },
});

function stickyScrollEnabled(page: Page): Promise<boolean> {
  return page.evaluate(() => {
    const host = window as unknown as {
      __WEAVIE_MONACO__: typeof import("monaco-editor");
      __WEAVIE_EDITOR__: import("monaco-editor").editor.IStandaloneCodeEditor;
    };
    return host.__WEAVIE_EDITOR__.getOption(host.__WEAVIE_MONACO__.editor.EditorOption.stickyScroll)
      .enabled;
  });
}

test("sticky scroll is off in files past the line limit, including once edits cross it", async ({
  page,
}) => {
  await openFile(page, "long.ts");
  await expect.poll(() => stickyScrollEnabled(page)).toBe(false);

  await openFile(page, "short.ts");
  await expect.poll(() => stickyScrollEnabled(page)).toBe(true);

  await page.locator(".monaco-editor .view-lines").click();
  await pressDocumentEnd(page);
  for (let i = 0; i < 15; i++) await page.keyboard.press("Enter");
  await expect.poll(() => stickyScrollEnabled(page)).toBe(false);
});
