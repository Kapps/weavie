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
      const send = (json: string): void => {
        sent.push(json);
        let message: { type?: string };
        try {
          message = JSON.parse(json) as { type?: string };
        } catch {
          return;
        }
        if (message.type !== "ready") return;

        // A native host can answer synchronously. Its ready replay must land after App's listener exists,
        // including the structured transcript that used to disappear during bootstrap.
        const push = window.__weavieReceive;
        push?.(
          JSON.stringify({
            type: "session-list",
            sessions: [
              {
                id: "cx",
                label: "codex",
                active: true,
                loaded: true,
                primary: true,
                providerId: "codex",
                agentSurface: "structured",
                status: "idle",
                hue: 200,
                monogram: "C",
              },
            ],
          }),
        );
        push?.(JSON.stringify({ type: "agent-pane-reset", slot: "cx", workspace: "/repo" }));
        push?.(
          JSON.stringify({
            type: "agent-pane-batch",
            slot: "cx",
            workspace: "/repo",
            messages: [
              {
                type: "item-completed",
                providerId: "codex",
                itemId: "answer",
                itemType: "agentMessage",
                status: "completed",
                text: "restored-on-ready",
              },
            ],
          }),
        );
      };
      let receive: ((event: { data: unknown }) => void) | null = null;
      const chrome = (window as unknown as { chrome?: Record<string, unknown> }).chrome ?? {};
      chrome.webview = {
        postMessage: send,
        addEventListener: (type: string, listener: (event: { data: unknown }) => void) => {
          if (type === "message") receive = listener;
        },
      };
      (window as unknown as { chrome: Record<string, unknown> }).chrome = chrome;
      (window as unknown as { __weavieHostPush: (raw: string) => void }).__weavieHostPush = (raw) =>
        receive?.({ data: raw });
      (window as unknown as { webkit: unknown }).webkit = {
        messageHandlers: {
          weavie: { postMessage: send },
        },
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

    await expect(page.locator(".agent-markdown")).toContainText("restored-on-ready");

    // Inbound: Windows' ordered WebView2 message event reaches the mounted app.
    const toast = page.locator(".toast-msg", { hasText: "hello-native" });
    await page.evaluate(() =>
      (window as unknown as { __weavieHostPush: (raw: string) => void }).__weavieHostPush(
        JSON.stringify({ type: "notify", level: "info", message: "hello-native" }),
      ),
    );
    await expect(toast).toBeVisible();

    await page.evaluate(() => {
      const push = (window as unknown as { __weavieHostPush: (raw: string) => void })
        .__weavieHostPush;
      for (let index = 0; index < 100; index += 1) {
        push(
          JSON.stringify({
            type: "notify",
            level: "info",
            message: `ordered-${index}`,
            key: "ordered-native",
          }),
        );
      }
    });
    await expect(page.locator(".toast-msg", { hasText: "ordered-99" })).toBeVisible();
  });
});
