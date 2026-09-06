import { writeFile } from "node:fs/promises";
import type { WebSocketRoute } from "@playwright/test";
import { ChunkedMessageReceiver } from "../../src/messaging/chunked-message";
import { type MessageEnvelope, parseEnvelope } from "../../src/messaging/message-envelope";
import { activeSessionSlot, createSession } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test("mobile resumes a loaded agent without transferring archived bodies @cross", async ({
  page,
}) => {
  await createSession(page, { branch: "lazy-history", provider: "fake-acp" });
  const slot = await activeSessionSlot(page);
  const surface = page.locator('[data-surface="structured-agent"]');
  const composer = surface.locator("[data-agent-composer] textarea");
  const submit = async (prompt: string): Promise<void> => {
    await composer.fill(prompt);
    await composer.press("Enter");
    await expect(surface.getByRole("button", { name: "Run", exact: true })).toBeVisible();
  };
  await submit("large-history");
  await expect(surface).toContainText("ARCHIVED_ASSISTANT_END");
  for (let turn = 0; turn < 16; turn++) {
    await submit(`Recent update ${turn}: the workspace is ready for review.`);
    await expect(surface.locator(".agent-entry-message.agent-tone-assistant").last()).toContainText(
      `echo: Recent update ${turn}`,
    );
  }

  const requests: MessageEnvelope[] = [];
  const responses: string[] = [];
  let bridge: WebSocketRoute | undefined;
  await page.routeWebSocket("**/*", (socket) => {
    bridge = socket;
    const server = socket.connectToServer();
    const receiver = new ChunkedMessageReceiver();
    socket.onMessage((data) => {
      const message = parseEnvelope(data.toString());
      if (
        message?.kind === "request" &&
        message.feature === "agent" &&
        message.session?.slot === slot
      )
        requests.push(message);
      server.send(data);
    });
    server.onMessage((data) => {
      const decoded = receiver.ingest(data.toString());
      const message = decoded === null ? null : parseEnvelope(decoded);
      if (
        message?.kind === "response" &&
        message.feature === "agent" &&
        message.session?.slot === slot
      ) {
        responses.push(decoded!);
      }
      socket.send(data);
    });
  });
  await page.setViewportSize({ width: 390, height: 844 });
  await page.reload({ waitUntil: "domcontentloaded" });
  await expect(page.locator("#splash")).toHaveCount(0);
  await page.getByRole("button", { name: "Agent", exact: true }).click();
  await expect(surface).toContainText("echo: Recent update 15");
  expect(responses.join("\n")).not.toContain("ARCHIVED_TOOL_BODY");
  expect(responses.join("\n")).not.toContain("ARCHIVED_ASSISTANT_BODY");
  const coldBytes = responses.reduce((bytes, response) => bytes + Buffer.byteLength(response), 0);
  expect(coldBytes).toBeLessThan(192 * 1024);
  expect(requests.filter((request) => request.name === "historyPage")).toHaveLength(1);
  const transfer = test.info().outputPath("cold-resume-transfer.json");
  await writeFile(
    transfer,
    JSON.stringify({ coldBytes, historyRequests: requests.map((request) => request.name) }),
  );
  await test.info().attach("cold-resume-transfer.json", {
    path: transfer,
    contentType: "application/json",
  });

  const body = surface.locator(".agent-body");
  await body.hover();
  await page.mouse.wheel(0, -100_000);
  const activity = surface.locator(".agent-entry-activity").first();
  await expect(activity).toBeInViewport();
  await activity.locator("summary").first().click();
  await expect(activity).toContainText("Archived diagnostic output");
  await expect(activity.getByRole("button", { name: "Review edit" })).toBeVisible();
  expect(responses.join("\n")).not.toContain("ARCHIVED_TOOL_BODY");
  await activity.getByText("show output", { exact: true }).click();
  await expect(activity.locator(".agent-tool-output")).toContainText("ARCHIVED_TOOL_END");
  expect(responses.join("\n")).toContain("ARCHIVED_TOOL_BODY");

  const bodiesBeforeReconnect = requests.filter((request) => request.name === "historyBody").length;
  const pagesBeforeReconnect = requests.filter((request) => request.name === "historyPage").length;
  if (bridge === undefined) throw new Error("No live bridge to reconnect");
  await bridge.close();
  await expect
    .poll(() => requests.filter((request) => request.name === "historyPage").length)
    .toBeGreaterThan(pagesBeforeReconnect);
  await expect(page.locator(".footer-network-problem")).toHaveCount(0);
  await expect(activity.locator(".agent-tool-output")).toContainText("ARCHIVED_TOOL_END");
  expect(requests.filter((request) => request.name === "historyBody")).toHaveLength(
    bodiesBeforeReconnect,
  );
});
