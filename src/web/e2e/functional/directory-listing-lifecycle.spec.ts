import { mkdir, writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { MessageEnvelope } from "../../src/messaging/message-envelope";
import { activeSessionSlot, createSession, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

let sent: MessageEnvelope[];
let heldDirectory: string;
let releaseListing: (() => Promise<void>) | undefined;

test.use({
  preNavigate: {
    run: async (page) => {
      sent = [];
      heldDirectory = "";
      releaseListing = undefined;
      const received = new Set<string>();
      await page.exposeFunction("listingResponseReceived", (requestId: string) => {
        received.add(requestId);
      });
      await page.addInitScript(() => {
        const Original = window.WebSocket;
        window.WebSocket = class extends Original {
          constructor(...args: ConstructorParameters<typeof WebSocket>) {
            super(...args);
            this.addEventListener("message", (event) => {
              const message = JSON.parse(event.data);
              if (message.kind === "response") {
                setTimeout(() => {
                  void (
                    window as unknown as {
                      listingResponseReceived: (requestId: string) => Promise<void>;
                    }
                  ).listingResponseReceived(message.requestId);
                }, 0);
              }
            });
          }
        };
      });
      await page.routeWebSocket("**/*", (socket) => {
        const server = socket.connectToServer();
        let heldRequest: string | null = null;
        socket.onMessage((data) => {
          const message = JSON.parse(data.toString()) as MessageEnvelope;
          sent.push(message);
          if (
            message.feature === "files" &&
            message.name === "listDirectory" &&
            (message.payload as { path: string }).path === heldDirectory
          ) {
            heldRequest = message.requestId;
            heldDirectory = "";
          }
          server.send(data);
        });
        server.onMessage((data) => {
          const message = JSON.parse(data.toString()) as MessageEnvelope;
          if (heldRequest !== null && message.requestId === heldRequest) {
            const requestId = heldRequest;
            heldRequest = null;
            releaseListing = async () => {
              socket.send(data);
              await expect.poll(() => received.has(requestId)).toBe(true);
            };
          } else socket.send(data);
        });
      });
    },
  },
});

function directoryMessages(name: string, path: string): MessageEnvelope[] {
  const subscriptions = new Set(
    sent
      .filter(
        (message) =>
          message.feature === "files" &&
          message.name === "listDirectory" &&
          (message.payload as { path: string }).path === path,
      )
      .map((message) => (message.payload as { subscriptionId: string }).subscriptionId),
  );
  return sent.filter(
    (message) =>
      message.feature === "files" &&
      message.name === name &&
      subscriptions.has((message.payload as { subscriptionId: string }).subscriptionId),
  );
}

test("switching sessions releases directory watches on their owning session", async ({
  page,
  weavie,
}) => {
  const directory = join(weavie.workspace, "owned-folder");
  await mkdir(directory);
  await writeFile(join(directory, "original-child.txt"), "original");
  const row = (name: string) => page.locator(".browser-row", { hasText: name });
  const originalSlot = await activeSessionSlot(page);
  await createSession(page, { branch: "e2e/directory-owner", provider: "claude" });
  const otherSlot = await activeSessionSlot(page);
  await page.locator(`.session-chip[data-session-slot="${originalSlot}"]`).click();
  await runCommand(page, "Toggle File Browser");
  await row("owned-folder").click();
  await expect(row("original-child.txt")).toBeVisible();
  const owner = directoryMessages("listDirectory", directory)[0].session;
  expect(owner?.slot).toBe(await activeSessionSlot(page));

  await page.locator(`.session-chip[data-session-slot="${otherSlot}"]`).click();
  expect(await activeSessionSlot(page)).not.toBe(owner?.slot);
  await expect.poll(() => directoryMessages("unwatchDirectory", directory).length).toBe(1);
  expect(directoryMessages("unwatchDirectory", directory)[0].session).toEqual(owner);

  await page.locator(`.session-chip[data-session-slot="${owner?.slot}"]`).click();
  await expect(row("owned-folder")).toBeVisible();
  await row("owned-folder").click();
  await expect(row("original-child.txt")).toBeVisible();
  await writeFile(join(directory, "after-switch.txt"), "watch restored");
  await expect(row("after-switch.txt")).toBeVisible();
});

test("a collapsed directory ignores its late listing and reopens with a live watch", async ({
  page,
  weavie,
}) => {
  const directory = join(weavie.workspace, "delayed-folder");
  await mkdir(directory);
  await writeFile(join(directory, "original-child.txt"), "original");
  heldDirectory = directory;
  const row = (name: string) => page.locator(".browser-row", { hasText: name });
  await runCommand(page, "Toggle File Browser");
  await row("delayed-folder").click();
  await expect.poll(() => releaseListing !== undefined).toBe(true);
  await expect(page.locator(".browser-children .browser-loading")).toBeVisible();
  await row("delayed-folder").click();
  await expect.poll(() => directoryMessages("unwatchDirectory", directory).length).toBe(1);
  if (releaseListing === undefined) throw new Error("No delayed listing was captured.");
  await releaseListing();
  const requestsBeforeReopen = directoryMessages("listDirectory", directory).length;
  await row("delayed-folder").click();
  await expect
    .poll(() => directoryMessages("listDirectory", directory).length)
    .toBeGreaterThan(requestsBeforeReopen);
  await expect(row("original-child.txt")).toBeVisible();
  await writeFile(join(directory, "created-after-reopen.txt"), "live watch");
  await expect(row("created-after-reopen.txt")).toBeVisible();
});

test("closing an omnibar path alias preserves the browser directory watch", async ({
  page,
  weavie,
}) => {
  const directory = join(weavie.workspace, "shared-folder");
  await writeFile(join(weavie.workspace, ".git", "info", "exclude"), "shared-folder/\n");
  await mkdir(directory);
  await writeFile(join(directory, "original-child.txt"), "original");
  const row = (name: string) => page.locator(".browser-row", { hasText: name });
  await runCommand(page, "Toggle File Browser");
  await row("shared-folder").click();
  await expect(row("original-child.txt")).toBeVisible();

  const omnibar = page.locator(".tb-omnibar-input");
  await omnibar.click();
  await omnibar.fill(`${directory}//`);
  await expect(page.locator(".tb-omnibar-row", { hasText: "original-child.txt" })).toBeVisible();
  expect(directoryMessages("listDirectory", `${directory}/`).length).toBeGreaterThan(0);
  await omnibar.press("Escape");
  await expect.poll(() => directoryMessages("unwatchDirectory", `${directory}/`).length).toBe(1);
  expect(directoryMessages("unwatchDirectory", directory)).toHaveLength(0);

  await writeFile(join(directory, "after-omnibar-close.txt"), "browser still watches");
  await expect(row("after-omnibar-close.txt")).toBeVisible();
});
