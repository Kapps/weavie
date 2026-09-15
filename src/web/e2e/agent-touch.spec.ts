import { fileURLToPath } from "node:url";
import { expect } from "@playwright/test";
import { test } from "./harness/network-fixtures";
import { MockHost, mockSession } from "./mock-host";

const session = mockSession("touch", "touch", "acp");
const distDir = fileURLToPath(new URL("../dist", import.meta.url));
let host: MockHost;

test.use({ hasTouch: true, isMobile: true, viewport: { width: 390, height: 844 } });
test.beforeEach(async ({ page }) => {
  host = await MockHost.start({ distDir, sessions: [session] });
  await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
  await host.waitUntilConnected();
  await page.getByRole("button", { name: "Agent", exact: true }).tap();
});
test.afterEach(async () => {
  await host.close();
});

test("a touchscreen tap opens the plan button inside a transcript row", async ({ page }) => {
  host.publishAgentPane(session.address, {
    providerId: "acp",
    type: "item-completed",
    threadId: "touch-thread",
    turnId: "touch-turn",
    itemId: "touch-plan",
    itemType: "plan",
    status: "completed",
    text: "# Touch plan\n\nOpen this plan.",
  });
  const open = page.getByRole("button", { name: "Open plan", exact: true });
  await expect(open).toBeVisible();
  await open.tap();
  await expect
    .poll(() =>
      host.received.some(
        (message) =>
          message.feature === "agent" && message.name === "openPlan" && message.kind === "request",
      ),
    )
    .toBe(true);
});

test("horizontal touch panning keeps a nested code block native", async ({ page }) => {
  host.publishAgentPane(session.address, {
    providerId: "acp",
    type: "item-completed",
    turnId: "touch-code-turn",
    itemId: "touch-code",
    itemType: "agentMessage",
    status: "completed",
    text: `\`\`\`text\n${"wide code content ".repeat(80)}\n\`\`\``,
  });
  const pre = page.locator(".agent-markdown pre");
  await expect(pre).toBeVisible();
  const bounds = await pre.boundingBox();
  if (bounds === null) throw new Error("Missing code block geometry");
  expect(await pre.evaluate((element) => element.scrollWidth > element.clientWidth)).toBe(true);
  const cdp = await page.context().newCDPSession(page);
  const y = bounds.y + bounds.height / 2;
  const x = bounds.x + Math.min(bounds.width - 20, 250);
  await cdp.send("Input.dispatchTouchEvent", { type: "touchStart", touchPoints: [{ x, y }] });
  for (let step = 1; step <= 8; step++) {
    await cdp.send("Input.dispatchTouchEvent", {
      type: "touchMove",
      touchPoints: [{ x: x - step * 20, y }],
    });
    await page.evaluate(
      () => new Promise<void>((resolve) => requestAnimationFrame(() => resolve())),
    );
  }
  await cdp.send("Input.dispatchTouchEvent", { type: "touchEnd", touchPoints: [] });
  await expect.poll(() => pre.evaluate((element) => element.scrollLeft)).toBeGreaterThan(50);
});

test("vertical touch panning moves the owned transcript", async ({ page }) => {
  for (let index = 0; index < 20; index++) {
    host.publishAgentPane(session.address, {
      providerId: "acp",
      type: "item-completed",
      turnId: `touch-turn-${index}`,
      itemId: `touch-message-${index}`,
      itemType: "agentMessage",
      status: "completed",
      text: `### Touch answer ${index}\n\n${"Transcript content for a vertical gesture. ".repeat(8)}`,
    });
  }
  await expect(page.getByText("Touch answer 19", { exact: true })).toBeVisible();
  const body = page.locator(".agent-body");
  const anchor = await body.evaluate((element) => {
    const bounds = element.getBoundingClientRect();
    const row = Array.from(element.querySelectorAll<HTMLElement>(".agent-virtual-row")).find(
      (candidate) => candidate.getBoundingClientRect().bottom > bounds.top,
    );
    if (row === undefined) throw new Error("Missing touch reading anchor");
    return { id: row.dataset.transcriptEntry, top: row.getBoundingClientRect().top };
  });
  const bounds = await body.boundingBox();
  if (bounds === null) throw new Error("Missing transcript geometry");
  const cdp = await page.context().newCDPSession(page);
  const x = bounds.x + bounds.width / 2;
  const y = bounds.y + bounds.height / 2;
  await cdp.send("Input.dispatchTouchEvent", { type: "touchStart", touchPoints: [{ x, y }] });
  for (let step = 1; step <= 8; step++) {
    await cdp.send("Input.dispatchTouchEvent", {
      type: "touchMove",
      touchPoints: [{ x, y: y + step * 15 }],
    });
    await page.evaluate(
      () => new Promise<void>((resolve) => requestAnimationFrame(() => resolve())),
    );
  }
  await expect
    .poll(() =>
      body.evaluate((element, initial) => {
        const row = Array.from(element.querySelectorAll<HTMLElement>(".agent-virtual-row")).find(
          (candidate) => candidate.dataset.transcriptEntry === initial.id,
        );
        if (row === undefined) throw new Error("Touch reading anchor was lost");
        return row.getBoundingClientRect().top - initial.top;
      }, anchor),
    )
    .toBeGreaterThan(90);
  await cdp.send("Input.dispatchTouchEvent", { type: "touchEnd", touchPoints: [] });
});
