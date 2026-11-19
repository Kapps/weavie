import { unlink, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { expect, test } from "../harness/fixtures";
import { decodeTestWebSocketMessage } from "../harness/websocket-codec";

// The page downloads a session's file list once and is then told each change, so a file created or deleted on
// disk reaches Go to File without the list being sent again.

test("a file created or deleted on disk reaches Go to File from one list download", async ({
  page,
  weavie,
}) => {
  const downloads: (string | null)[] = [];
  await page.route("**/weavie-file-index?**", async (route) => {
    downloads.push(new URL(route.request().url()).searchParams.get("knownRevision"));
    await route.continue();
  });
  await page.reload({ waitUntil: "domcontentloaded" });
  const input = page.locator(".tb-omnibar-input");
  const rows = page.locator(".tb-omnibar-list .tb-omnibar-row");
  await input.click();
  await input.fill("README");
  await expect(rows.filter({ hasText: "README.md" }).first()).toBeVisible();

  const created = join(weavie.workspace, "sync-probe-created.ts");
  await writeFile(created, "export {};\n");
  await input.fill("sync-probe-created");
  await expect(rows.filter({ hasText: "sync-probe-created.ts" })).toHaveCount(1);

  await unlink(created);
  await expect(page.locator(".tb-omnibar-empty")).toHaveText("No matching files");
  expect(downloads).toEqual([null]);
});

test("a change published while the list downloads is applied to it", async ({ page, weavie }) => {
  let published = 0;
  await page.routeWebSocket("**/*", (socket) => {
    const server = socket.connectToServer();
    socket.onMessage((data) => server.send(data));
    server.onMessage((data) => {
      const message = JSON.parse(decodeTestWebSocketMessage(data));
      if (
        message.feature === "files" &&
        message.name === "indexChanged" &&
        message.payload.added.includes("sync-probe-during.ts")
      )
        published += 1;
      socket.send(data);
    });
  });
  let listed = (): void => {};
  let release = (): void => {};
  const snapshot = new Promise<void>((resolve) => {
    listed = resolve;
  });
  const held = new Promise<void>((resolve) => {
    release = resolve;
  });
  await page.route("**/weavie-file-index?**", async (route) => {
    const response = await route.fetch();
    listed();
    await held;
    await route.fulfill({ response });
  });
  await page.reload({ waitUntil: "domcontentloaded" });
  await snapshot;

  // The host listed its files before this one existed, and announces it while the page still waits for that list.
  await writeFile(join(weavie.workspace, "sync-probe-during.ts"), "export {};\n");
  await expect.poll(() => published).toBe(1);
  const input = page.locator(".tb-omnibar-input");
  await input.click();
  await expect(page.locator(".tb-omnibar-empty")).toHaveText("Loading files…");
  release();

  await input.fill("sync-probe-during");
  await expect(
    page.locator(".tb-omnibar-list .tb-omnibar-row").filter({ hasText: "sync-probe-during.ts" }),
  ).toHaveCount(1);
});
