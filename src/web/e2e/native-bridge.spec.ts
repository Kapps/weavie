import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test } from "@playwright/test";
import { MockHost } from "./mock-host";

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

  test("round-trips the same host and exact-session envelopes as WebSocket", async ({ page }) => {
    await page.addInitScript(() => {
      interface Address {
        slot: string;
        incarnation: string;
      }
      interface Envelope {
        scope: "host" | "session";
        session: Address | null;
        kind: "event" | "request" | "response" | "cancel";
        requestId: string | null;
        feature: string;
        name: string;
        payload: unknown;
        error: string | null;
      }

      const address = { slot: "cx", incarnation: "cx-incarnation" };
      const sent: string[] = [];
      (window as unknown as { __weavieSent: string[] }).__weavieSent = sent;
      let receive: ((event: { data: unknown }) => void) | null = null;
      const push = (message: Envelope): void => {
        const raw = JSON.stringify(message);
        if (receive !== null) {
          receive({ data: raw });
        } else {
          window.__weavieReceive?.(raw);
        }
      };
      const event = (
        scope: "host" | "session",
        session: Address | null,
        feature: string,
        name: string,
        payload: unknown,
      ): Envelope => ({
        scope,
        session,
        kind: "event",
        requestId: null,
        feature,
        name,
        payload,
        error: null,
      });
      const respond = (request: Envelope, payload: unknown): void =>
        push({
          ...request,
          kind: "response",
          payload,
          error: null,
        });
      const send = (json: string): void => {
        sent.push(json);
        let message: Envelope;
        try {
          message = JSON.parse(json) as Envelope;
        } catch {
          return;
        }
        if (
          message.kind === "request" &&
          message.scope === "host" &&
          message.feature === "connection" &&
          message.name === "hello"
        ) {
          respond(message, {
            hostIncarnation: "native-host",
            buildNumber: "test",
            sessions: [
              {
                id: "cx",
                label: "codex",
                address,
                loaded: true,
                providerId: "codex",
                agentSurface: "structured",
                agentInputProtocol: 2,
                status: "idle",
                hue: 200,
                monogram: "C",
              },
            ],
            layout: {
              root: { type: "pane", id: "p_agent", kind: "terminal:claude" },
              focused: "p_agent",
            },
            remoteAgents: [],
            rail: { lastLocation: "local", promoted: [], selected: null },
            search: {
              options: {
                caseSensitive: false,
                wholeWord: false,
                regex: false,
                excludeGitignored: true,
                include: "",
                exclude: "",
              },
              recentTerms: [],
            },
            testProfile: "",
            commandCatalog: { commands: [], keybindings: [] },
          });
        } else if (
          message.kind === "request" &&
          message.scope === "session" &&
          message.feature === "lifecycle" &&
          message.name === "sync"
        ) {
          respond(message, { ok: true });
        } else if (
          message.kind === "request" &&
          message.scope === "session" &&
          message.feature === "agent" &&
          message.name === "historyPage"
        ) {
          const record = {
            generation: 0,
            ordinal: 1,
            revision: 1,
            textOffset: 0,
            textLength: 21,
            type: "item-completed",
            providerId: "codex",
            itemId: "answer",
            itemType: "agentMessage",
            status: "completed",
            text: "restored-from-history",
          };
          const json = JSON.stringify(record);
          respond(message, {
            generation: 0,
            restarted: false,
            messages: [
              {
                generation: 0,
                ordinal: 1,
                revision: 1,
                jsonOffset: 0,
                jsonLength: json.length,
                json,
              },
            ],
            cursor: null,
          });
        }
      };

      const chrome = (window as unknown as { chrome?: Record<string, unknown> }).chrome ?? {};
      chrome.webview = {
        postMessage: send,
        addEventListener: (type: string, listener: (event: { data: unknown }) => void) => {
          if (type === "message") {
            receive = listener;
          }
        },
      };
      (window as unknown as { chrome: Record<string, unknown> }).chrome = chrome;
      (window as unknown as { __weavieHostEvent: typeof event }).__weavieHostEvent = (
        scope,
        session,
        feature,
        name,
        payload,
      ) => push(event(scope, session, feature, name, payload));
      (window as unknown as { webkit: unknown }).webkit = {
        messageHandlers: { weavie: { postMessage: send } },
      };
    });

    await page.goto(`${host.url}/`, { waitUntil: "domcontentloaded" });

    await expect
      .poll(async () => {
        const sent = await page.evaluate(
          () => (window as unknown as { __weavieSent: string[] }).__weavieSent ?? [],
        );
        return sent.some((raw) => {
          const message = JSON.parse(raw) as {
            scope?: string;
            kind?: string;
            feature?: string;
            name?: string;
          };
          return (
            message.scope === "host" &&
            message.kind === "request" &&
            message.feature === "connection" &&
            message.name === "hello"
          );
        });
      })
      .toBe(true);
    await expect(page.locator(".agent-markdown")).toContainText("restored-from-history");

    const pushHostNotification = (message: string, key?: string): Promise<void> =>
      page.evaluate(
        ({ message, key }) => {
          const push = (
            window as unknown as {
              __weavieHostEvent: (
                scope: "host",
                session: null,
                feature: string,
                name: string,
                payload: unknown,
              ) => void;
            }
          ).__weavieHostEvent;
          push("host", null, "notifications", "show", { level: "info", message, key });
        },
        { message, key },
      );

    await pushHostNotification("hello-native");
    await expect(page.locator(".toast-msg", { hasText: "hello-native" })).toBeVisible();

    await page.evaluate(() => {
      const push = (
        window as unknown as {
          __weavieHostEvent: (
            scope: "host",
            session: null,
            feature: string,
            name: string,
            payload: unknown,
          ) => void;
        }
      ).__weavieHostEvent;
      for (let index = 0; index < 100; index += 1) {
        push("host", null, "notifications", "show", {
          level: "info",
          message: `ordered-${index}`,
          key: "ordered-native",
        });
      }
    });
    await expect(page.locator(".toast-msg", { hasText: "ordered-99" })).toBeVisible();
  });
});
