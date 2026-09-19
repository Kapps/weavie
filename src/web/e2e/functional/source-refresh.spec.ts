import { existsSync } from "node:fs";
import { rename, writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { decodeTestWebSocketMessage } from "../harness/websocket-codec";

const URL = "https://www.notion.so/Refresh-Doc-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d";
const DOC = { title: "Refresh Doc", markdown: "Original paragraph." };
const replies = new WeakMap<Page, number>();

test.use({
  notionDoc: DOC,
  preNavigate: {
    run: async (page) => {
      replies.set(page, 0);
      page.on("websocket", (socket) => {
        socket.on("framereceived", ({ payload }) => {
          const message = JSON.parse(decodeTestWebSocketMessage(payload));
          if (
            message.feature === "sources" &&
            message.name === "refresh" &&
            message.kind === "response"
          ) {
            replies.set(page, (replies.get(page) ?? 0) + 1);
          }
        });
      });
    },
  },
});

async function openDoc(page: Page) {
  await runCommand(page, "Open URL…");
  await page.locator(".url-prompt-input").fill(URL);
  await page.locator(".url-prompt-input").press("Enter");
  const source = page.locator(".editor-source");
  await expect(source.locator(".wv-title")).toHaveText(DOC.title);
  return source;
}

async function externalChange(home: string, markdown: string) {
  const path = join(home, "fake-notion.json.external.json");
  await writeFile(`${path}.tmp`, JSON.stringify({ markdown }));
  await rename(`${path}.tmp`, path);
}

async function focusWindow(page: Page) {
  await page.evaluate(() => window.dispatchEvent(new Event("focus")));
}

async function releaseRefresh(page: Page, home: string) {
  const before = replies.get(page) ?? 0;
  await writeFile(join(home, "fake-notion.json.fetch-release"), "");
  await expect.poll(() => replies.get(page)).toBe(before + 1);
  await page.evaluate(() => new Promise<void>((resolve) => requestAnimationFrame(() => resolve())));
}

test("refreshes remote changes and preserves a conflicting local draft", async ({
  page,
  weavie,
}) => {
  await page.clock.install();
  const source = await openDoc(page);
  await externalChange(weavie.home, "Updated in Notion while the page was open.");
  await page.clock.fastForward(15_000);
  await expect(source.locator("p")).toHaveText("Updated in Notion while the page was open.");
  await externalChange(weavie.home, "Another update while away.");
  await focusWindow(page);
  await expect(source.locator("p")).toHaveText("Another update while away.");
  await source.locator("p").click();
  const editor = source.locator(".wv-block-editor");
  await editor.fill("Uncommitted local work.");
  await externalChange(weavie.home, "The original paragraph changed remotely.");
  await page.clock.fastForward(30_000);
  await focusWindow(page);
  await expect(editor).toHaveValue("Uncommitted local work.");
  await editor.press("Enter");
  await expect(source.locator(".wv-edit-error")).toContainText("changed in Notion");
  await page.clock.fastForward(15_000);
  await expect(editor).toHaveValue("Uncommitted local work.");
  await expect(editor).toBeEnabled();
  await editor.press("Escape");
  await focusWindow(page);
  await expect(source.locator("p")).toHaveText("The original paragraph changed remotely.");
});

test.describe("in-flight refresh", () => {
  test.use({ notionDoc: { ...DOC, holdFetchAt: 2 } });

  test("a refresh started before a save cannot roll back the saved document", async ({
    page,
    weavie,
  }) => {
    const source = await openDoc(page);
    await focusWindow(page);
    await expect
      .poll(() => existsSync(join(weavie.home, "fake-notion.json.fetch-entered")))
      .toBe(true);
    await source.locator("p").click();
    const editor = source.locator(".wv-block-editor");
    await editor.fill("Saved while the old refresh was in flight.");
    await editor.press("Enter");
    await expect(editor).toHaveCount(0);
    await expect(source.locator("p")).toHaveText("Saved while the old refresh was in flight.");
    await releaseRefresh(page, weavie.home);
    await expect(source.locator("p")).toHaveText("Saved while the old refresh was in flight.");
  });
});
