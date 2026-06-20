import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test } from "@playwright/test";
import { MockHost } from "./mock-host";

// The built app produced by `vite build`. The e2e run builds it first (see the `e2e` npm script).
const distDir = join(dirname(fileURLToPath(import.meta.url)), "..", "dist");

test.beforeAll(() => {
  if (!existsSync(join(distDir, "index.html"))) {
    throw new Error(
      `built app not found at ${distDir}; run \`pnpm run build\` before the e2e tests`,
    );
  }
});

test.describe("remote bridge transport", () => {
  let host: MockHost;

  test.beforeEach(async () => {
    host = await MockHost.start({ distDir });
  });

  test.afterEach(async () => {
    await host.close();
  });

  test("a plain browser connects over WebSocket and round-trips bridge messages", async ({
    page,
  }) => {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });

    // Outbound: main.tsx posts { type: "ready" } at module load, before the WebSocket has opened. It must
    // still arrive — proving the transport buffers pre-open sends and flushes them on connect.
    const ready = await host.waitForMessage("ready");
    expect(ready.type).toBe("ready");

    // Inbound: the host pushes a user-facing notify; the app must render it as a toast — proving a
    // WebSocket frame reaches deliverFromHost -> onHostMessage -> App's notify handler in a real browser.
    // Re-pushed under toPass because App registers its host listener only after it mounts (post first
    // paint), so the first push can land before the listener exists; the retry closes that startup race.
    const toast = page.locator(".toast-msg", { hasText: "hello-from-mock-host" });
    await expect(async () => {
      host.pushToWeb({ type: "notify", level: "info", message: "hello-from-mock-host" });
      await expect(toast).toBeVisible({ timeout: 1000 });
    }).toPass({ timeout: 20_000 });
  });

  test("the bridge stays silent in a plain browser with no host advertised", async ({ page }) => {
    // No `?weavie-bridge=` and no injected bridge global (the mock host injects the other bootstrap globals,
    // like the real serve host, but never advertises a bridge): the transport must resolve to "none" — the
    // page boots, posts nothing over the (absent) bridge, and never throws. Guards the no-bridge path.
    const pageErrors: string[] = [];
    page.on("pageerror", (error) => pageErrors.push(error.message));

    await page.goto(`${host.url}/`, { waitUntil: "domcontentloaded" });
    await page.waitForTimeout(1500);

    expect(host.received).toHaveLength(0);
    expect(pageErrors, `unexpected page errors: ${pageErrors.join("; ")}`).toHaveLength(0);
  });
});
