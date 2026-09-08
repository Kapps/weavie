import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { openFile, runCommand } from "../harness/actions";
import { writeFakeScript } from "../harness/fake-claude";
import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";
import type { HeadlessHost } from "../harness/weavie-host";

const ORIGINAL = "just plain text\n";
const CHANGED = `${ORIGINAL}keep this addition\n`;

async function expectClosed(page: Page): Promise<void> {
  await expect(page.locator(".editor-review-close")).toHaveCount(0);
  await expect(page.locator(".editor-review-toggle")).toHaveCount(0);
  await expect(page.locator(".unified-review")).toHaveCount(0);
  await expect(page.locator(".weavie-inline-toolbar")).toHaveCount(0);
  await expect(page.locator(".weavie-inline-added, .weavie-inline-accepted")).toHaveCount(0);
}

test.describe("close diff", () => {
  test.use({ fakeScript: { steps: appliedEdit("notes.txt", CHANGED) } });

  test("closing pending unified review keeps changes and stays closed after unload and restart", async ({
    page,
    weavie,
  }) => {
    test.slow();
    await openFile(page, "notes.txt");
    await expect(page.locator(".weavie-inline-added")).toBeVisible();
    await page.locator(".editor-review-toggle").click();
    await expect(page.locator(".unified-review")).toBeVisible();
    const close = page.locator(".editor-review-close");
    await expect(close).toHaveAttribute("title", /Accept remaining changes and close diff.*\(/);
    await close.click();
    await expectClosed(page);
    expect(await readFile(join(weavie.workspace, "notes.txt"), "utf8")).toBe(CHANGED);

    await writeFakeScript(weavie.home, []);
    await runCommand(page, "Unload Session");
    await expect(page.locator(".session-chip.unloaded")).toHaveCount(1);
    await page.locator(".session-chip.unloaded").click();
    await expect(page.locator(".session-chip.unloaded")).toHaveCount(0);
    await openFile(page, "notes.txt");
    await expectClosed(page);

    await page.goto("about:blank");
    await (weavie as HeadlessHost).restart();
    const connect = await page.request.post(weavie.url, {
      form: { token: weavie.token },
      maxRedirects: 0,
    });
    expect(connect.status()).toBe(302);
    await page.goto(weavie.url);
    await expect(page.locator("#splash")).toHaveCount(0);
    await openFile(page, "notes.txt");
    await expectClosed(page);
    expect(await readFile(join(weavie.workspace, "notes.txt"), "utf8")).toBe(CHANGED);
  });

  for (const decision of ["Keep All Changes", "Undo All Changes"]) {
    test(`the fully reviewed state closes after ${decision}`, async ({ page, weavie }) => {
      await openFile(page, "notes.txt");
      await expect(page.locator(".weavie-inline-added")).toBeVisible();
      await page.locator(".editor-review-toggle").click();
      await runCommand(page, decision);
      if (decision === "Undo All Changes") {
        await page.getByRole("button", { name: "Revert all", exact: true }).click();
      }
      await expect(page.locator(".weavie-inline-added")).toHaveCount(0);
      await expect(page.locator(".unified-review")).toBeVisible();
      await page.locator(".editor-review-close").click();
      await expectClosed(page);
      expect(await readFile(join(weavie.workspace, "notes.txt"), "utf8")).toBe(
        decision === "Keep All Changes" ? CHANGED : ORIGINAL,
      );
    });
  }
});

test("file review closes from the keyboard and the same comparison opens fresh", async ({
  page,
  weavie,
}) => {
  await writeFile(join(weavie.workspace, "notes.txt"), CHANGED);
  await runCommand(page, "Diff Against HEAD");
  await expect(page.locator(".weavie-inline-added")).toBeVisible();
  await page.keyboard.press("ControlOrMeta+Alt+w");
  await expectClosed(page);
  await runCommand(page, "Diff Against HEAD");
  await expect(page.locator(".weavie-inline-added")).toBeVisible();
  await expect(page.locator(".weavie-inline-accepted")).toHaveCount(0);
  await runCommand(page, "Close Diff");
  await expectClosed(page);
  expect(await readFile(join(weavie.workspace, "notes.txt"), "utf8")).toBe(CHANGED);
});

test.describe("agent edits after closing", () => {
  const next = `${CHANGED}a later agent addition\n`;
  const signal = ".weavie-e2e-next-edit";
  test.use({
    fakeScript: {
      steps: [
        ...appliedEdit("notes.txt", CHANGED),
        { op: "waitFile", path: `{{WORKSPACE}}/${signal}` },
        { op: "hook", request: { hook_event_name: "UserPromptSubmit" } },
        ...appliedEdit("notes.txt", next),
      ],
    },
  });

  test("new edits review only the changes made after closing", async ({ page, weavie }) => {
    await openFile(page, "notes.txt");
    await expect(page.locator(".weavie-inline-added")).toBeVisible();
    await page.locator(".editor-review-close").click();
    await expectClosed(page);
    await writeFile(join(weavie.workspace, signal), "");
    await expect(page.locator(".weavie-inline-added")).toBeVisible();
    await runCommand(page, "Undo All Changes");
    await page.getByRole("button", { name: "Revert all", exact: true }).click();
    await expect.poll(() => readFile(join(weavie.workspace, "notes.txt"), "utf8")).toBe(CHANGED);
  });
});
