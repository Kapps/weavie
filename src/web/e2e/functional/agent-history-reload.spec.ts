import type { WebSocketRoute } from "@playwright/test";
import { activeSessionSlot, createSession, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test("incomplete HTTP history can be reloaded while agent messaging stays connected @cross", async ({
  page,
}) => {
  await createSession(page, { branch: "history-reload", provider: "fake-acp" });
  const slot = await activeSessionSlot(page);
  const composer = page.locator("[data-agent-composer] textarea");
  const transcript = page.locator('[data-surface="structured-agent"]');
  await composer.fill("remember this response");
  await composer.press("Enter");
  await expect(transcript).toContainText("echo: remember this response");

  let requests = 0;
  await page.route("**/weavie-agent-history?**", async (route) => {
    if (new URL(route.request().url()).searchParams.get("slot") !== slot) {
      await route.continue();
      return;
    }
    requests++;
    if (requests !== 1) {
      await route.continue();
      return;
    }
    const response = await route.fetch();
    const firstBatch = (await response.text()).split("\n")[0]!;
    await route.fulfill({ response, body: `${firstBatch}\n` });
  });
  await page.reload({ waitUntil: "domcontentloaded" });
  const failure = page.locator(".toast-msg", { hasText: "History for" });
  await expect(failure).toContainText("incomplete");
  await expect(failure).toContainText("Reload Agent History");
  await expect(transcript).toContainText("echo: remember this response");
  await expect(page.locator(".footer-network-problem")).toHaveCount(0);

  await composer.fill("messaging still works");
  await composer.press("Enter");
  await expect(transcript).toContainText("echo: messaging still works");
  expect(requests).toBe(1);
  await composer.focus();
  await runCommand(page, "Reload Agent History");
  await expect.poll(() => requests).toBe(2);
  await expect(failure).toHaveCount(0);
  await expect(transcript).toContainText("echo: remember this response");
  await expect(transcript).toContainText("echo: messaging still works");
  await expect(page.locator(".footer-network-problem")).toHaveCount(0);
});

test("reconnect uses completed HTTP history revision without duplicating live output @cross", async ({
  page,
}) => {
  await createSession(page, { branch: "history-reconnect", provider: "fake-acp" });
  const slot = await activeSessionSlot(page);
  const composer = page.locator("[data-agent-composer] textarea");
  const transcript = page.locator('[data-surface="structured-agent"]');
  await composer.fill("loaded history");
  await composer.press("Enter");
  await expect(transcript).toContainText("echo: loaded history");

  let bridge: WebSocketRoute | undefined;
  const reads: Array<{ knownRevision: string | null; revision: number }> = [];
  await page.routeWebSocket("**/*", (socket) => {
    bridge = socket;
    socket.connectToServer();
  });
  await page.route("**/weavie-agent-history?**", async (route) => {
    const url = new URL(route.request().url());
    if (url.searchParams.get("slot") !== slot) {
      await route.continue();
      return;
    }
    const response = await route.fetch();
    const batch = JSON.parse((await response.text()).split("\n")[0]!);
    reads.push({ knownRevision: url.searchParams.get("knownRevision"), revision: batch.revision });
    await route.fulfill({ response });
  });
  await page.reload({ waitUntil: "domcontentloaded" });
  await expect(transcript).toContainText("echo: loaded history");
  await composer.fill("new live response");
  await composer.press("Enter");
  await expect(transcript).toContainText("echo: new live response");
  await bridge!.close();
  await expect.poll(() => reads.length).toBe(2);
  expect(reads[1]!.knownRevision).toBe(String(reads[0]!.revision));
  await expect(page.locator(".footer-network-problem")).toHaveCount(0);
  await expect(
    transcript.locator(".agent-tone-assistant", { hasText: "echo: loaded history" }),
  ).toHaveCount(1);
  await expect(
    transcript.locator(".agent-tone-assistant", { hasText: "echo: new live response" }),
  ).toHaveCount(1);
});
