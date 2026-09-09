import { readFile, unlink } from "node:fs/promises";
import { EOL } from "node:os";
import { join } from "node:path";
import type { Locator, Page } from "@playwright/test";
import {
  activeSessionSlot,
  createSession,
  openFile,
  pressDocumentStart,
  runCommand,
} from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { awaitReviewSet } from "../harness/navigator";
import { appliedEdit } from "../harness/review";

const source = "review-actions.ts";
const content =
  '// first review comment\n// second review comment\n// third review comment\n// fourth review comment\ngreet("review selection");\n';
test.use({
  inference: "success",
  automaticInference: true,
  permissions: ["clipboard-read", "clipboard-write"],
  fakeScript: { steps: appliedEdit(source, content) },
});
async function prepare(page: Page): Promise<Locator> {
  await awaitReviewSet(page, [source]);
  await openFile(page, "notes.txt");
  await page.locator(".editor-review-open").click();
  const section = page.locator(".unified-review-file", {
    has: page.locator(".unified-review-file-name", { hasText: source }),
  });
  await section
    .locator(".view-line")
    .first()
    .click({ position: { x: 4, y: 4 } });
  await pressDocumentStart(page);
  for (let i = 0; i < 4; i++) await page.keyboard.press("Shift+ArrowDown");
  return section;
}
for (const trigger of ["context menu", "shortcut"]) {
  test(`review ${trigger} revises the selected review file`, async ({ page, weavie }) => {
    const section = await prepare(page);
    const notesBefore = await readFile(join(weavie.workspace, "notes.txt"), "utf8");
    if (trigger === "context menu") {
      await page.evaluate(() => navigator.clipboard.writeText("unrelated clipboard"));
      await section
        .locator(".view-line")
        .first()
        .click({ button: "right", position: { x: 45, y: 4 } });
      await expect(page.locator(".context-menu")).toBeVisible();
      await page.locator(".context-menu-item").filter({ hasText: /^Copy/ }).click();
      await expect
        .poll(() => page.evaluate(() => navigator.clipboard.readText()))
        .toBe(content.split("\n").slice(0, 4).join(EOL));
      await section
        .locator(".view-line")
        .first()
        .click({ button: "right", position: { x: 45, y: 4 } });
      const revise = page.locator(".context-menu-item").filter({ hasText: /^Revise Selection/ });
      await expect(revise).toContainText(
        process.platform === "darwin" ? "⌘+Shift+E" : "Ctrl+Shift+E",
      );
      await revise.click();
    } else {
      await page.keyboard.press("ControlOrMeta+Shift+e");
    }
    const prompt = page.locator(".session-prompt-input");
    await expect(prompt).toBeFocused();
    await prompt.fill("Shorten the selected review comment to one line");
    await prompt.press("Enter");
    await expect
      .poll(() => readFile(join(weavie.workspace, source), "utf8"))
      .toBe('// revised by the fake\ngreet("review selection");\n');
    await expect(section.locator(".view-lines")).toContainText("// revised by the fake");
    expect(await readFile(join(weavie.workspace, "notes.txt"), "utf8")).toBe(notesBefore);
    await expect(page.locator(".weavie-revising-pill")).toHaveCount(0);
  });
}
test.describe("pending review revision ownership", () => {
  let confirmation: PromiseWithResolvers<() => void>;
  let requested = false;
  let canceled = false;
  let retired = false;
  test.use({
    preNavigate: {
      async run(page) {
        confirmation = Promise.withResolvers<() => void>();
        requested = false;
        canceled = false;
        retired = false;
        await page.routeWebSocket("**/*", (socket) => {
          const server = socket.connectToServer();
          server.onMessage((data) => {
            const message = JSON.parse(data.toString());
            if (message.feature === "revise") {
              if (message.kind === "cancel") canceled = true;
              if (requested && message.name === "state" && message.payload.regions.length === 0)
                retired = true;
            }
            socket.send(data);
          });
          socket.onMessage((data) => {
            const message = JSON.parse(data.toString());
            if (message.kind === "response" && message.feature === "revise") {
              requested = true;
              confirmation.resolve(() => server.send(data));
            } else server.send(data);
          });
        });
      },
    },
  });
  test("switching session cancels pending review confirmation without editing either session", async ({
    page,
    weavie,
  }) => {
    await prepare(page);
    const origin = await activeSessionSlot(page);
    await page.keyboard.press("ControlOrMeta+Shift+e");
    const prompt = page.locator(".session-prompt-input");
    await prompt.fill("Shorten the selected review comment");
    await prompt.press("Enter");
    await expect.poll(() => requested).toBe(true);
    await expect(page.locator(".unified-review .weavie-revising-pill")).toBeVisible();
    await createSession(page, { branch: "e2e/review-revise-owner", provider: "claude" });
    expect(await activeSessionSlot(page)).not.toBe(origin);
    await awaitReviewSet(page, [source]);
    await openFile(page, source);
    const incomingPath = await page.locator(".editor").getAttribute("data-active-file");
    if (incomingPath === null) throw new Error("Incoming file is not open");
    expect(incomingPath).not.toBe(join(weavie.workspace, source));
    const incomingBefore = await readFile(incomingPath, "utf8");
    const incomingSelection = await page.evaluate(() => window.__WEAVIE_EDITOR__.getSelections());
    await expect.poll(() => canceled && retired).toBe(true);
    (await confirmation.promise)();
    expect(await readFile(join(weavie.workspace, source), "utf8")).toBe(content);
    expect(await readFile(incomingPath, "utf8")).toBe(incomingBefore);
    expect(await page.evaluate(() => window.__WEAVIE_EDITOR__.getSelections())).toEqual(
      incomingSelection,
    );
    await expect(page.locator(".editor")).toHaveAttribute("data-active-file", incomingPath);
  });
});

test.describe("deleted review snapshots", () => {
  test.use({ fakeScript: null });
  test("snapshot context menu keeps Copy available and disables mutating actions", async ({
    page,
    weavie,
  }) => {
    const path = join(weavie.workspace, "notes.txt");
    await unlink(path);
    await runCommand(page, "Diff Against HEAD");
    await page.locator(".editor-empty-review").click();
    const section = page.locator(".unified-review-file", {
      has: page.locator(".unified-review-file-name", { hasText: "notes.txt" }),
    });
    await expect(section.locator(".monaco-editor")).toBeVisible();
    await section
      .locator(".view-line")
      .first()
      .click({ position: { x: 4, y: 4 } });
    await section
      .locator(".view-line")
      .first()
      .click({ button: "right", position: { x: 4, y: 4 } });
    const menu = page.locator(".context-menu");
    await expect(menu).toBeVisible();
    for (const label of ["Revise Selection", "Cut", "Paste", "Rename Symbol"]) {
      await expect(menu.locator(".context-menu-item", { hasText: label })).toBeDisabled();
    }
    await expect(menu.locator(".context-menu-item").filter({ hasText: /^Copy/ })).toBeEnabled();
    await expect(readFile(path, "utf8")).rejects.toMatchObject({ code: "ENOENT" });
    await expect(page.locator(".session-prompt-input")).toHaveCount(0);
  });
});
