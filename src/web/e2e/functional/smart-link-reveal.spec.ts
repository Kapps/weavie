import { mkdir, writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { awaitEditorReady } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Smart link matching, full stack: a clicked terminal file link whose relative path doesn't resolve is
// recovered by suffix match against the workspace index — one hit opens the file (line preserved), several
// open Go-to-File preloaded with the term (text selected) listing the candidates. The host recovery is
// unit-covered (FileOpenerTests/PathSuffixMatcherTests); this pins the cross-layer contract no unit test
// sees: canvas link click → reveal-file → recovery → open-file / focus-omnibar → omnibar preload+selection.

test.use({
  fakeScript: {
    // Short lines: the claude pane is ~50 cols, and a soft-wrapped link can't be clicked as one row.
    steps: [{ op: "print", text: "fix: services/payment.ts:9\r\nboth: config.ts:1\r\n" }],
  },
});

// The buffer position of `needle` in the claude pane's xterm (viewport row/col + grid size), or null.
function findClaudeLink(page: Page, needle: string) {
  return page.evaluate((n) => {
    const entry = Object.entries(window.__WEAVIE_TERMINALS__ ?? {}).find(([key]) =>
      key.endsWith(":claude"),
    );
    if (!entry) {
      return null;
    }
    const term = entry[1];
    const buf = term.buffer.active;
    for (let i = 0; i < buf.length; i++) {
      const col = (buf.getLine(i)?.translateToString(true) ?? "").indexOf(n);
      if (col >= 0) {
        return { row: i - buf.viewportY, col, cols: term.cols, rows: term.rows };
      }
    }
    return null;
  }, needle);
}

// Click `needle` inside the claude pane. Terminal links render in canvas (no DOM anchor), so the click point
// is computed from the xterm buffer position and the live cell metrics — the same path a user's pointer takes.
async function clickClaudeLink(page: Page, needle: string): Promise<void> {
  await expect.poll(() => findClaudeLink(page, needle), { timeout: 30_000 }).not.toBeNull();
  const pos = await findClaudeLink(page, needle);
  if (pos === null) {
    throw new Error(`link text vanished from claude terminal: ${needle}`);
  }
  const box = await page
    .locator('.terminal-surface[data-kind="terminal:claude"] .xterm-screen')
    .boundingBox();
  if (box === null) {
    throw new Error("claude terminal canvas not visible");
  }
  await page.mouse.click(
    box.x + (pos.col + needle.length / 2) * (box.width / pos.cols),
    box.y + (pos.row + 0.5) * (box.height / pos.rows),
  );
}

test("a link missing its leading folders opens the unique suffix match at its line", async ({
  page,
  weavie,
}) => {
  // The workspace file the link under-specifies: `services/payment.ts` for src/services/payment.ts.
  await mkdir(join(weavie.workspace, "src", "services"), { recursive: true });
  await writeFile(
    join(weavie.workspace, "src", "services", "payment.ts"),
    Array.from({ length: 10 }, (_, i) => `export const line${i + 1} = ${i + 1};`).join("\n"),
  );
  await awaitEditorReady(page);

  await clickClaudeLink(page, "services/payment.ts:9");

  await expect(page.locator(".editor")).toHaveAttribute(
    "data-active-file",
    /[\\/]src[\\/]services[\\/]payment\.ts$/,
  );
  // The link's :9 rides the recovery — the reveal lands on that line, not line 1.
  await expect
    .poll(() => page.evaluate(() => window.__WEAVIE_EDITOR__?.getPosition()?.lineNumber))
    .toBe(9);
});

test("an ambiguous bare filename opens Go-to-File preloaded with the term and lists the candidates", async ({
  page,
  weavie,
}) => {
  for (const dir of ["client", "server"]) {
    await mkdir(join(weavie.workspace, "src", dir), { recursive: true });
    await writeFile(join(weavie.workspace, "src", dir, "config.ts"), `export const ${dir} = 1;\n`);
  }
  await awaitEditorReady(page);

  await clickClaudeLink(page, "config.ts:1");

  // Go-to-File opens preloaded with the normalized term…
  const input = page.locator(".tb-omnibar-input");
  await expect(input).toHaveValue("config.ts");
  // …with the text selected, so typing replaces it instead of appending.
  await expect
    .poll(() =>
      input.evaluate((el: HTMLInputElement) => (el.selectionEnd ?? 0) - (el.selectionStart ?? 0)),
    )
    .toBe("config.ts".length);

  // Exactly the two candidates are listed.
  const rows = page.locator(".tb-omnibar-row", { hasText: "config.ts" });
  await expect(rows).toHaveCount(2);
  const dirs = (await page.locator(".tb-omnibar-row .tb-row-dir").allInnerTexts()).map((d) =>
    d.replaceAll("\\", "/"),
  );
  expect(dirs.sort()).toEqual(["src/client", "src/server"]);

  // Picking one (keyboard, like a user) opens exactly that file.
  await input.press("ArrowDown");
  const picked = (await page.locator(".tb-omnibar-row.selected .tb-row-dir").innerText())
    .trim()
    .replaceAll("\\", "/");
  await input.press("Enter");
  await expect(page.locator(".editor")).toHaveAttribute(
    "data-active-file",
    new RegExp(`[\\\\/]${picked.replaceAll("/", "[\\\\/]")}[\\\\/]config\\.ts$`),
  );
});
