import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test } from "@playwright/test";
import { MockHost, mockSession } from "./mock-host";

const distDir = join(dirname(fileURLToPath(import.meta.url)), "..", "dist");

test.beforeAll(() => {
  if (!existsSync(join(distDir, "index.html"))) {
    throw new Error(
      `built app not found at ${distDir}; run \`pnpm run build\` before the e2e tests`,
    );
  }
});

test("Linux middle-click autoscrolls the agent transcript and responds live", async ({ page }) => {
  const session = mockSession("autoscroll", "autoscroll", "codex");
  await page.addInitScript(() => {
    window.__WEAVIE_SHELL__ = {
      platform: "linux",
      titleBar: "linux",
      workspaceLabel: "autoscroll-test",
      recents: [],
      buildNumber: "test",
    };
  });
  const host = await MockHost.start({ distDir, sessions: [session] });
  host.setAgentHistory(session.address, {
    generation: 1,
    pageSize: 100,
    messages: Array.from({ length: 80 }, (_, index) => ({
      providerId: "codex",
      type: "item-completed",
      itemId: `message-${index}`,
      itemType: "agentMessage",
      status: "completed",
      text: `Agent transcript entry ${index}\n\n${"Scrollable content. ".repeat(12)}`,
    })),
  });

  try {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    const body = page.locator(".agent-body");
    await expect(body).toBeVisible();
    await expect.poll(() => body.evaluate((element) => element.scrollTop)).toBeGreaterThan(0);
    const initialTop = await body.evaluate((element) => element.scrollTop);
    const bounds = await body.boundingBox();
    if (bounds === null) {
      throw new Error("agent transcript has no bounds");
    }
    const origin = { x: bounds.x + bounds.width / 2, y: bounds.y + bounds.height / 2 };

    await page.mouse.click(origin.x, origin.y, { button: "middle" });
    await expect(body).toHaveClass(/agent-middle-click-autoscrolling/);
    await page.mouse.move(origin.x, origin.y - 100);
    await expect.poll(() => body.evaluate((element) => element.scrollTop)).toBeLessThan(initialTop);
    await page.keyboard.press("Escape");
    await expect(body).not.toHaveClass(/agent-middle-click-autoscrolling/);

    await page.mouse.move(origin.x, origin.y);
    await page.mouse.down({ button: "middle" });
    await page.mouse.move(origin.x, origin.y + 100);
    await page.mouse.up({ button: "middle" });
    await expect(body).not.toHaveClass(/agent-middle-click-autoscrolling/);

    await page.mouse.click(origin.x, origin.y, { button: "middle" });
    await expect(body).toHaveClass(/agent-middle-click-autoscrolling/);
    host.publishHost("settings", "agent-defaults", {
      defaultProvider: "claude",
      middleClickAutoscroll: false,
      providers: [],
    });
    await expect(body).not.toHaveClass(/agent-middle-click-autoscrolling/);
    await page.mouse.click(origin.x, origin.y, { button: "middle" });
    await expect(body).not.toHaveClass(/agent-middle-click-autoscrolling/);
  } finally {
    await host.close();
  }
});
