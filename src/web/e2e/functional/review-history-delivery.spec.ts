import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { clickIntoEditor, openFile, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";
import { decodeTestWebSocketMessage } from "../harness/websocket-codec";

const pauseHistory = new WeakMap<Page, () => void>();

const baseline = ["first", ...Array.from({ length: 10 }, (_, i) => `context ${i}`), "last"].join(
  "\n",
);
const changed = baseline.replace("first", "updated first").replace("last", "updated last");

test.use({
  workspaceSeed: { run: (workspace) => writeFile(join(workspace, "notes.txt"), baseline) },
  fakeScript: { steps: appliedEdit("notes.txt", changed) },
  preNavigate: {
    async run(page) {
      let paused = false;
      pauseHistory.set(page, () => {
        paused = true;
      });
      await page.routeWebSocket("**/*", (socket) => {
        const server = socket.connectToServer();
        server.onMessage((data) => {
          const message = JSON.parse(decodeTestWebSocketMessage(data));
          // Hold history availability after undo; file mutations and command replies still arrive.
          if (
            paused &&
            message.kind === "event" &&
            message.feature === "review" &&
            message.name === "history"
          )
            return;
          socket.send(data);
        });
      });
    },
  },
});

test("undo revert survives redo before history availability arrives", async ({ page, weavie }) => {
  await openFile(page, "notes.txt");
  const pending = page.locator(".weavie-inline-pending-keep");
  const reverted = changed.replace("updated last", "last");
  const contents = () => readFile(join(weavie.workspace, "notes.txt"), "utf8");
  await expect(pending).toHaveCount(2);
  await page.locator(".weavie-inline-pending-revert").last().click();
  await expect(pending).toHaveCount(1);
  await expect.poll(contents).toBe(reverted);
  await expect(page.locator(".weavie-inline-hist").first()).toBeEnabled();
  await runCommand(page, "Undo Revert (Review)");
  await expect.poll(contents).toBe(changed);
  await expect(page.locator(".weavie-inline-hist").nth(1)).toBeEnabled();

  pauseHistory.get(page)!();
  await runCommand(page, "Redo Review Action");
  await expect.poll(contents).toBe(reverted);
  await expect(pending).toHaveCount(1);
  await runCommand(page, "Undo Revert (Review)");
  await expect.poll(contents).toBe(changed);
  await expect(pending).toHaveCount(2);

  await runCommand(page, "Close All Editors");
  await expect(page.locator(".editor-empty")).toBeVisible();
  await runCommand(page, "Redo Review Action");
  await expect.poll(contents).toBe(reverted);
  await runCommand(page, "Undo Revert (Review)");
  await expect.poll(contents).toBe(changed);
});

test.describe("ordinary editing without review history", () => {
  const requests = new WeakMap<Page, string[]>();
  test.use({
    fakeScript: { steps: [] },
    preNavigate: {
      async run(page) {
        const reviewCommands: string[] = [];
        requests.set(page, reviewCommands);
        page.on("websocket", (socket) => {
          socket.on("framesent", ({ payload }) => {
            const message = JSON.parse(decodeTestWebSocketMessage(payload));
            if (
              message.kind === "request" &&
              message.feature === "review" &&
              (message.name === "undo" || message.name === "redo")
            )
              reviewCommands.push(message.name);
          });
        });
      },
    },
  });

  test("the undo-keep chord remains Monaco's insert-line-above shortcut", async ({ page }) => {
    await openFile(page, "notes.txt");
    const reviewCommands = requests.get(page)!;
    reviewCommands.length = 0;
    await clickIntoEditor(page);
    await page.evaluate(() => window.__WEAVIE_EDITOR__!.setPosition({ lineNumber: 2, column: 1 }));
    await page.keyboard.press("ControlOrMeta+Shift+Enter");
    expect(await page.evaluate(() => window.__WEAVIE_EDITOR__!.getModel()!.getValue())).toBe(
      baseline.replace("first\n", "first\n\n"),
    );
    expect(reviewCommands).toEqual([]);
  });
});
