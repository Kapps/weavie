import { mkdir, writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { awaitEditorReady, expectRevealed, openCommandPalette, openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { type HeldResponse, holdHostResponse } from "../harness/held-response";

// Canvas links resolve through the host's suffix matcher; only the invoking client presents the file or
// candidate palette. Both paths preserve the requested line and reject stale presentation intent.

test.use({
  fakeScript: {
    // Short lines: the claude pane is ~50 cols, and a soft-wrapped link can't be clicked as one row.
    steps: [
      { op: "waitFile", path: "{{WORKSPACE}}/.show-smart-links" },
      { op: "print", text: "fix: services/payment.ts:9\r\nboth: config.ts:3\r\n" },
    ],
  },
});

// Root agents may start before the browser attaches; gate fake output because it cannot repaint on PTY resize.
const showClaudeLinks = (workspace: string): Promise<void> =>
  writeFile(join(workspace, ".show-smart-links"), "");

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

async function createAmbiguousConfigs(workspace: string): Promise<void> {
  for (const dir of ["client", "server"]) {
    await mkdir(join(workspace, "src", dir), { recursive: true });
    await writeFile(
      join(workspace, "src", dir, "config.ts"),
      `// ${dir} config\n// settings\nexport const ${dir} = 1;\n`,
    );
  }
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
  await showClaudeLinks(weavie.workspace);

  await clickClaudeLink(page, "services/payment.ts:9");

  // The link's :9 rides the recovery — the reveal lands on that line, not line 1.
  await expectRevealed(page, "src/services/payment.ts", 9);
});

test("an ambiguous bare filename opens Go-to-File preloaded with the term and lists the candidates", async ({
  page,
  weavie,
}) => {
  await createAmbiguousConfigs(weavie.workspace);
  await awaitEditorReady(page);
  await showClaudeLinks(weavie.workspace);

  await clickClaudeLink(page, "config.ts:3");

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

  // Picking one (keyboard, like a user) opens exactly that file, at the link's line — the `:3` rides the
  // ambiguity resolution, not just the direct-open path.
  await input.press("ArrowDown");
  const picked = (await page.locator(".tb-omnibar-row.selected .tb-row-dir").innerText())
    .trim()
    .replaceAll("\\", "/");
  await input.press("Enter");
  await expectRevealed(page, `${picked}/config.ts`, 3);
});

test.describe("delayed reference ambiguity", () => {
  const replies = new WeakMap<Page, HeldResponse>();
  test.use({
    preNavigate: {
      run: async (page) => {
        replies.set(page, await holdHostResponse(page));
      },
    },
  });

  test("an older ambiguous link cannot replace a newer command palette", async ({
    page,
    weavie,
  }) => {
    await createAmbiguousConfigs(weavie.workspace);
    await openFile(page, "hello.ts");
    await showClaudeLinks(weavie.workspace);
    const reply = replies.get(page)!;
    reply.hold((message) => message.feature === "files" && message.name === "resolveReference");
    await clickClaudeLink(page, "config.ts:3");
    await expect
      .poll(() => reply.received()?.payload)
      .toEqual({ kind: "ambiguous", query: "config.ts", line: 3 });
    await openCommandPalette(page);
    const input = page.locator(".tb-omnibar-input");
    await input.fill(">Go Back");
    await expect(input).toBeFocused();
    const reading = await page.evaluate(() => ({
      model: window.__WEAVIE_EDITOR__?.getModel()?.uri.toString(),
      position: window.__WEAVIE_EDITOR__?.getPosition(),
    }));
    await reply.release();
    await expect(input).toBeFocused();
    await expect(input).toHaveValue(">Go Back");
    await expect(page.locator(".tb-omnibar-box")).toHaveClass(/\bopen\b/);
    expect(
      await page.evaluate(() => ({
        model: window.__WEAVIE_EDITOR__?.getModel()?.uri.toString(),
        position: window.__WEAVIE_EDITOR__?.getPosition(),
      })),
    ).toEqual(reading);
  });
});
