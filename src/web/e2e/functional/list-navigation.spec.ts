import { execFileSync } from "node:child_process";
import { writeFileSync } from "node:fs";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// The list-navigation primitive's unconditional behaviors, at two call sites with different DOM. Unit tests
// (list-navigation.test.ts) own the index math; only a real build can show that the `nav.row(i)` spread
// actually reaches the rendered row — the `data-list-row` address it scrolls by, and the hover that moves the
// highlight — and that a real overflowing list really scrolls.

// More rows than any viewport fits, so the highlight leaves the visible window well before the list ends.
function seedFiles(workspace: string): void {
  for (let i = 0; i < 80; i++) {
    const name = `scrollcheck-${String(i).padStart(2, "0")}.ts`;
    writeFileSync(join(workspace, name), `export const n = ${i};\n`);
  }
}

// The highlighted row's position inside its own scroll viewport: both slacks >= 0 means fully visible.
function rowVisibility(page: Page, list: string, row: string) {
  return page.evaluate(
    ([listSelector, rowSelector]) => {
      const container = document.querySelector(listSelector);
      const selected = document.querySelector(rowSelector);
      if (container === null || selected === null) {
        return null;
      }
      const outer = container.getBoundingClientRect();
      const inner = selected.getBoundingClientRect();
      return {
        scrollTop: Math.round(container.scrollTop),
        scrolls: container.scrollHeight > container.clientHeight,
        address: selected.getAttribute("data-list-row"),
        topSlack: Math.round(inner.top - outer.top),
        bottomSlack: Math.round(outer.bottom - inner.bottom),
      };
    },
    [list, row] as const,
  );
}

const omnibarRow = (page: Page) =>
  rowVisibility(page, ".tb-omnibar-list", ".tb-omnibar-row.selected");

test("the omnibar keeps the arrow-key highlight scrolled into view", async ({ weavie, page }) => {
  seedFiles(weavie.workspace);
  const input = page.locator(".tb-omnibar-input");
  await input.click();
  await input.fill("scrollcheck");
  await expect(page.locator(".tb-omnibar-row")).toHaveCount(80);

  const start = await omnibarRow(page);
  expect(start?.address).toMatch(/^\d+:0$/); // the nav.row() spread reached the rendered row
  expect(start?.scrolls).toBe(true); // more rows than fit, so a stuck list would strand the highlight

  for (let i = 0; i < 40; i++) {
    await input.press("ArrowDown");
  }
  await expect.poll(async () => (await omnibarRow(page))?.address).toMatch(/:40$/);
  const down = await omnibarRow(page);
  expect(down?.scrollTop).toBeGreaterThan(0);
  expect(down?.topSlack).toBeGreaterThanOrEqual(0);
  expect(down?.bottomSlack).toBeGreaterThanOrEqual(0);

  for (let i = 0; i < 40; i++) {
    await input.press("ArrowUp");
  }
  const up = await omnibarRow(page);
  expect(up?.address).toMatch(/:0$/);
  expect(up?.topSlack).toBeGreaterThanOrEqual(0);
  expect(up?.bottomSlack).toBeGreaterThanOrEqual(0);
});

test("hovering an omnibar row moves the highlight, and Enter acts on the hovered row", async ({
  weavie,
  page,
}) => {
  seedFiles(weavie.workspace);
  const input = page.locator(".tb-omnibar-input");
  await input.click();
  await input.fill("scrollcheck");
  await expect(page.locator(".tb-omnibar-row")).toHaveCount(80);

  for (let i = 0; i < 6; i++) {
    await input.press("ArrowDown");
  }
  await expect.poll(async () => (await omnibarRow(page))?.address).toMatch(/:6$/);

  const target = page.locator(".tb-omnibar-row").nth(2);
  const name = ((await target.locator(".tb-row-leaf").textContent()) ?? "").trim();
  const box = await target.boundingBox();
  if (box === null) {
    throw new Error("the hover target row has no box");
  }
  // Two positions, because the highlight follows mousemove — a cursor that never moves must not steal it.
  await page.mouse.move(box.x + 20, box.y + box.height / 2 - 1);
  await page.mouse.move(box.x + 24, box.y + box.height / 2);
  await expect.poll(async () => (await omnibarRow(page))?.address).toMatch(/:2$/);

  await input.press("Enter");
  await expect(page.locator(".editor-tab", { hasText: name })).toBeVisible();
});

test("arrow keys on an empty omnibar list never reach the input", async ({ page }) => {
  const input = page.locator(".tb-omnibar-input");
  await input.click();
  await input.fill("zzqqxxnosuchfile");
  await expect(page.locator(".tb-omnibar-empty")).toHaveText("No matching files");

  await input.evaluate((el) => (el as HTMLInputElement).setSelectionRange(5, 5));
  const caret = () => input.evaluate((el) => (el as HTMLInputElement).selectionStart);
  await input.press("ArrowDown");
  expect(await caret()).toBe(5);
  await input.press("ArrowUp");
  expect(await caret()).toBe(5);
});

test("the branch typeahead scrolls its highlight into view", async ({ weavie, page }) => {
  test.setTimeout(60_000);
  // More branches than the 8-suggestion cap, so the popup overflows its own max-height.
  const head = execFileSync("git", ["rev-parse", "HEAD"], { cwd: weavie.workspace })
    .toString()
    .trim();
  for (let i = 0; i < 12; i++) {
    execFileSync("git", ["branch", `scrolltarget-${String(i).padStart(2, "0")}`, head], {
      cwd: weavie.workspace,
      stdio: "ignore",
    });
  }

  await runCommand(page, "Diff Against…");
  const prompt = page.locator(".session-prompt");
  const input = prompt.locator(".session-prompt-input");
  await input.fill("scrolltarget");
  await expect(prompt.locator(".session-prompt-suggestion")).toHaveCount(8);

  const suggestion = () =>
    rowVisibility(page, ".session-prompt-suggestions", ".session-prompt-suggestion.active");
  expect(
    await page.locator(".session-prompt-suggestion").first().getAttribute("data-list-row"),
  ).toMatch(/^\d+:0$/);

  // The highlight starts at -1 (Enter submits the typed text), so the first press lands on row 0.
  for (let i = 0; i < 8; i++) {
    await input.press("ArrowDown");
  }
  const last = await suggestion();
  expect(last?.address).toMatch(/:7$/);
  expect(last?.scrollTop).toBeGreaterThan(0);
  expect(last?.topSlack).toBeGreaterThanOrEqual(0);
  expect(last?.bottomSlack).toBeGreaterThanOrEqual(0);
});
