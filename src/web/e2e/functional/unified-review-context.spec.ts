import { once } from "node:events";
import { readdir, readFile } from "node:fs/promises";
import { join } from "node:path";
import WebSocket from "ws";
import { openFile, pressDocumentStart } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { awaitReviewSet } from "../harness/navigator";
import { appliedEdit } from "../harness/review";

test.use({
  preNavigate: { run: (page) => page.clock.install() },
  fakeScript: {
    steps: [
      ...appliedEdit("hello.ts", "export const answer = 42;\nexport const greeting = 'hello';\n"),
      ...appliedEdit("notes.txt", "a changed note\nanother changed note\n"),
    ],
  },
});

// Query the real IDE server: the result is exactly the context available to the agent.
async function connectSelectionReader(home: string) {
  const directory = join(home, ".claude", "ide");
  const locks = (await readdir(directory)).filter((name) => name.endsWith(".lock"));
  expect(locks).toHaveLength(1);
  const lock = JSON.parse(await readFile(join(directory, locks[0]), "utf8"));
  const socket = new WebSocket(`ws://127.0.0.1:${locks[0].replace(".lock", "")}/`, "mcp", {
    headers: { "x-claude-code-ide-authorization": lock.authToken },
  });
  await once(socket, "open");
  let nextId = 0;
  const request = (method: string, params: Record<string, unknown>) =>
    new Promise<{ content: { text: string }[] }>((resolve, reject) => {
      const id = ++nextId;
      const cleanup = () => {
        socket.off("message", receive);
        socket.off("error", fail);
        socket.off("close", closed);
      };
      const fail = (error: Error) => {
        cleanup();
        reject(error);
      };
      const closed = () => fail(new Error("The IDE socket closed before replying."));
      const receive = (data: WebSocket.RawData) => {
        const response = JSON.parse(data.toString());
        if (response.id !== id) return;
        cleanup();
        if (response.error) reject(new Error(JSON.stringify(response.error)));
        else resolve(response.result);
      };
      socket.on("message", receive);
      socket.once("error", fail);
      socket.once("close", closed);
      socket.send(JSON.stringify({ jsonrpc: "2.0", id, method, params }));
    });
  await request("initialize", {
    protocolVersion: "2025-03-26",
    capabilities: {},
    clientInfo: { name: "selection-regression", version: "1" },
  });
  socket.send(JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" }));
  return {
    read: async () => {
      const result = await request("tools/call", { name: "getCurrentSelection", arguments: {} });
      return JSON.parse(result.content[0].text);
    },
    openFile: (filePath: string) =>
      request("tools/call", { name: "openFile", arguments: { filePath } }),
    close: () => socket.close(),
  };
}

test("unified diff clicks and selections become the agent's current editor context", async ({
  page,
  weavie,
}) => {
  await awaitReviewSet(page, ["hello.ts", "notes.txt"]);
  await openFile(page, "hello.ts");
  const context = await connectSelectionReader(weavie.home);
  try {
    await page.locator(".editor-review-toggle").click();
    const section = (name: string) =>
      page.locator(".unified-review-file", {
        has: page.locator(".unified-review-file-name", { hasText: name }),
      });
    const notes = section("notes.txt");
    await notes.locator(".view-line", { hasText: "a changed note" }).click({
      position: { x: 4, y: 4 },
    });
    await pressDocumentStart(page);
    await expect.poll(context.read).toMatchObject({
      success: true,
      filePath: join(weavie.workspace, "notes.txt"),
      text: "",
      selection: { start: { line: 0, character: 0 }, isEmpty: true },
    });

    await page.keyboard.press("Shift+ArrowDown");
    const selected = {
      filePath: join(weavie.workspace, "notes.txt"),
      text: "a changed note\n",
      selection: {
        start: { line: 0, character: 0 },
        end: { line: 1, character: 0 },
        isEmpty: false,
      },
    };
    await expect.poll(context.read).toMatchObject(selected);
    await page.locator('.terminal-surface[data-kind="terminal:claude"] .pane-head').click();
    await expect.poll(context.read).toMatchObject(selected);

    await context.openFile(join(weavie.workspace, "long.ts"));
    await expect(page.locator(".editor")).toHaveAttribute("data-active-file", /[\\/]long\.ts$/);
    await page.clock.runFor(200);
    await expect(page.locator(".unified-review")).toBeVisible();
    await expect.poll(context.read).toMatchObject(selected);
    await page.clock.resume();

    await section("hello.ts")
      .locator(".view-line")
      .first()
      .click({
        position: { x: 4, y: 4 },
      });
    await expect.poll(context.read).toMatchObject({
      filePath: join(weavie.workspace, "hello.ts"),
      text: "",
    });
    // Focusing the input preserves its caret and selection, unlike clicking the code.
    await notes.getByRole("textbox", { name: "Editor content" }).focus();
    await expect.poll(context.read).toMatchObject(selected);

    const firstLine = notes.locator(".view-line", { hasText: "a changed note" });
    const secondLine = notes.locator(".view-line", { hasText: "another changed note" });
    await firstLine.scrollIntoViewIfNeeded();
    const start = await firstLine.boundingBox();
    const end = await secondLine.boundingBox();
    if (!start || !end) throw new Error("The selected review lines must be visible.");
    await page.mouse.move(start.x + 4, start.y + 4);
    await page.mouse.down();
    await page.mouse.move(end.x + 4, end.y + 4, { steps: 8 });
    await page.mouse.up();
    await expect.poll(context.read).toMatchObject(selected);

    await page.locator(".editor-review-toggle").click();
    await expect(page.locator(".unified-review")).toHaveCount(0);
    await openFile(page, "hello.ts");
    for (const toast of await page
      .locator(".toast", { hasText: "typescript language intelligence is unavailable" })
      .all()) {
      await toast.locator(".toast-close").click();
    }
    await page
      .locator(".editor .view-line", { hasText: "export const answer = 42;" })
      .click({ position: { x: 80, y: 9 } });
    await expect(
      page.locator(".editor").getByRole("textbox", { name: "Editor content" }),
    ).toBeFocused();
    await pressDocumentStart(page);
    for (let character = 0; character < "export const answer = 42;".length; character++) {
      await page.keyboard.press("Shift+ArrowRight");
    }
    await expect.poll(context.read).toMatchObject({
      filePath: join(weavie.workspace, "hello.ts"),
      text: "export const answer = 42;",
    });
  } finally {
    context.close();
  }
});
