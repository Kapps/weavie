import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, type Locator, type Page, test } from "@playwright/test";
import { MockHost, mockSession } from "./mock-host";

const distDir = join(dirname(fileURLToPath(import.meta.url)), "..", "dist");

test.beforeAll(() => {
  if (!existsSync(join(distDir, "index.html"))) {
    throw new Error(
      `built app not found at ${distDir}; run \`pnpm run build\` before the e2e tests`,
    );
  }
});

// Both tests need the same Linux-shelled workspace holding one scrollable transcript.
async function openAutoscrollPane(
  page: Page,
  name: string,
): Promise<{ host: MockHost; body: Locator }> {
  const session = mockSession(name, name, "codex");
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
  await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
  await host.waitUntilConnected();
  const body = page.locator(".agent-body");
  await expect(body).toBeVisible();
  return { host, body };
}

// The transcript's center, where a middle click starts an autoscroll.
async function paneOrigin(body: Locator): Promise<{ x: number; y: number }> {
  const bounds = await body.boundingBox();
  if (bounds === null) {
    throw new Error("agent transcript has no bounds");
  }
  return { x: bounds.x + bounds.width / 2, y: bounds.y + bounds.height / 2 };
}

test("Linux middle-click autoscrolls the agent transcript and responds live", async ({ page }) => {
  const { host, body } = await openAutoscrollPane(page, "autoscroll");

  try {
    await expect.poll(() => body.evaluate((element) => element.scrollTop)).toBeGreaterThan(0);
    const initialTop = await body.evaluate((element) => element.scrollTop);
    const origin = await paneOrigin(body);

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

// A wheel listener on window or document — passive or not — takes the page off WebKit's async-scrolling
// path, so every surface scrolls on the main thread. The autoscroll's must exist only while it is running.
test("the autoscroll registers no global wheel listener unless it is running", async ({ page }) => {
  await page.addInitScript(() => {
    let live = 0;
    (window as unknown as { __wheelListeners: () => number }).__wheelListeners = () => live;
    const add = EventTarget.prototype.addEventListener;
    const remove = EventTarget.prototype.removeEventListener;
    const global = (target: EventTarget): boolean =>
      target === window || target === document || target === document.body;
    // These listeners are torn down by AbortController, which never calls removeEventListener.
    EventTarget.prototype.addEventListener = function (type, listener, options) {
      if (type === "wheel" && global(this)) {
        live++;
        (options as { signal?: AbortSignal } | undefined)?.signal?.addEventListener("abort", () => {
          live--;
        });
      }
      add.call(this, type, listener, options);
    };
    EventTarget.prototype.removeEventListener = function (type, listener, options) {
      if (type === "wheel" && global(this)) live--;
      remove.call(this, type, listener, options);
    };
  });
  const { host, body } = await openAutoscrollPane(page, "autoscroll-listeners");
  const wheelListeners = () =>
    page.evaluate(() =>
      (window as unknown as { __wheelListeners: () => number }).__wheelListeners(),
    );

  try {
    expect(await wheelListeners()).toBe(0);

    const origin = await paneOrigin(body);
    await page.mouse.click(origin.x, origin.y, { button: "middle" });
    await expect(body).toHaveClass(/agent-middle-click-autoscrolling/);
    expect(await wheelListeners()).toBeGreaterThan(0);

    await page.keyboard.press("Escape");
    await expect(body).not.toHaveClass(/agent-middle-click-autoscrolling/);
    await expect.poll(wheelListeners).toBe(0);
  } finally {
    await host.close();
  }
});
