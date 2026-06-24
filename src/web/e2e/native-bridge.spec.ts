import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test } from "@playwright/test";
import { MockHost } from "./mock-host";

// Native (Win/Mac/Linux) hosts deliver the bridge over an in-process WebView script-message channel, which
// Playwright can't drive against a real shell. The web bundle is identical across hosts, so what native
// actually needs proven is that the in-process channel honors the same message contract as the WSS bridge.
// This injects a fake channel (window.webkit.messageHandlers.weavie + window.__weavieReceive) and checks
// both directions — the conformance carve-out from docs/specs/integration-testing-strategy.md.

const distDir = join(dirname(fileURLToPath(import.meta.url)), "..", "dist");

test.beforeAll(() => {
  if (!existsSync(join(distDir, "index.html"))) {
    throw new Error(`built app not found at ${distDir}; run \`pnpm run build\` first`);
  }
});

test.describe("native in-process bridge contract", () => {
  let host: MockHost;

  test.beforeEach(async () => {
    host = await MockHost.start({ distDir });
  });

  test.afterEach(async () => {
    await host.close();
  });

  test("the in-process channel round-trips bridge messages both ways", async ({ page }) => {
    // Inject the native channel before any app script runs, so bridge.ts picks the in-process transport
    // (no ?weavie-bridge, so the WebSocket path is never chosen).
    await page.addInitScript(() => {
      const sent: string[] = [];
      (window as unknown as { __weavieSent: string[] }).__weavieSent = sent;
      (window as unknown as { webkit: unknown }).webkit = {
        messageHandlers: { weavie: { postMessage: (json: string) => sent.push(json) } },
      };
    });

    await page.goto(`${host.url}/`, { waitUntil: "domcontentloaded" });

    // Outbound: the page posts { type: "ready" } through the in-process channel.
    await expect
      .poll(async () => {
        const sent = await page.evaluate(
          () => (window as unknown as { __weavieSent: string[] }).__weavieSent ?? [],
        );
        return sent.some((s) => {
          try {
            return (JSON.parse(s) as { type?: string }).type === "ready";
          } catch {
            return false;
          }
        });
      })
      .toBe(true);

    // Inbound: a host push via window.__weavieReceive reaches the app, which renders the toast. Re-pushed
    // under toPass because App registers its host listener only after mount.
    const toast = page.locator(".toast-msg", { hasText: "hello-native" });
    await expect(async () => {
      await page.evaluate(() =>
        (window as unknown as { __weavieReceive: (raw: string) => void }).__weavieReceive(
          JSON.stringify({ type: "notify", level: "info", message: "hello-native" }),
        ),
      );
      await expect(toast).toBeVisible({ timeout: 1000 });
    }).toPass({ timeout: 20_000 });
  });
});
