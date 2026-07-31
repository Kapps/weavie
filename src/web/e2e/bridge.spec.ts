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

test.describe("session-addressed WebSocket transport", () => {
  let host: MockHost;

  test.beforeEach(async () => {
    host = await MockHost.start({ distDir });
  });

  test.afterEach(async () => {
    await host.close();
  });

  test("connects with a host-scoped hello and receives host events", async ({ page }) => {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    const hello = await host.waitUntilConnected();
    expect(hello).toMatchObject({
      scope: "host",
      kind: "request",
      feature: "connection",
      name: "hello",
      session: null,
    });

    host.publishHost("notifications", "show", {
      level: "info",
      message: "hello-from-mock-host",
    });
    await expect(page.locator(".toast-msg", { hasText: "hello-from-mock-host" })).toBeVisible();
  });

  test("live fonts update normal DOM and session-owned source typography", async ({ page }) => {
    const session = mockSession("source", "source", "codex", true);
    host.setSessions([session]);
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();

    host.publishSession(session.address, "editor", "openOverlay", {
      path: "typography-source",
      kind: "source",
    });
    host.publishSession(session.address, "sources", "loading", {
      target: "typography-source",
      title: "Typography",
      sourceId: "notion",
    });
    host.publishSession(session.address, "sources", "document", {
      target: "typography-source",
      title: "Typography",
      sourceId: "notion",
      markdown: "Body with `code`.",
      editedTime: "",
    });
    host.publishSession(session.address, "sources", "promptToken", {
      sourceId: "notion",
      label: "Notion",
    });

    const prose = page.locator(".editor-source .wv-source");
    const sourceCode = prose.locator("code");
    const promptInput = page.locator(".session-prompt-input");
    await expect(sourceCode).toBeVisible();
    await expect(promptInput).toBeVisible();

    host.publishHost("settings", "fonts", {
      editor: { family: '"Courier New", monospace', size: 21, weight: "700" },
      terminal: { family: "monospace", size: 13, weight: "normal" },
    });

    await expect
      .poll(async () => {
        const [content, prompt, proseStyle] = await Promise.all(
          [sourceCode, promptInput, prose].map((locator) =>
            locator.evaluate((element) => {
              const style = getComputedStyle(element);
              return { family: style.fontFamily, size: style.fontSize, weight: style.fontWeight };
            }),
          ),
        );
        return {
          contentFamily: content.family,
          contentWeight: content.weight,
          promptFamily: prompt.family,
          promptWeight: prompt.weight,
          proseFamily: proseStyle.family,
          proseSize: proseStyle.size,
          proseWeight: proseStyle.weight,
        };
      })
      .toEqual({
        contentFamily: '"Courier New", monospace',
        contentWeight: "700",
        promptFamily: '"Courier New", monospace',
        promptWeight: "700",
        proseFamily: "Chivo, system-ui, sans-serif",
        proseSize: "21px",
        proseWeight: "400",
      });
  });

  test("selection binds the exact session view without a host-side switch", async ({ page }) => {
    const main = mockSession("main", "main", "claude", true);
    const feature = mockSession("feature", "feature", "codex", false);
    host.setSessions([main, feature]);
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    await host.waitForSession(main.address, "event", "view", "attach");

    const checkpoint = host.checkpoint();
    await page.locator('.session-chip[title^="feature —"]').click();
    const attached = await host.waitForSession(
      feature.address,
      "event",
      "view",
      "attach",
      checkpoint,
    );

    expect(attached.session).toEqual(feature.address);
    expect(
      host.received
        .slice(checkpoint)
        .filter(
          (message) =>
            message.scope === "host" && message.feature === "sessions" && message.name === "switch",
        ),
    ).toEqual([]);
    await expect(page.locator(".session-chip.active")).toHaveAttribute("title", /^feature —/);
  });

  test("a background editor event updates its owner before that session is selected", async ({
    page,
  }) => {
    const selected = mockSession("selected", "selected", "codex", true);
    const background = mockSession("background", "background", "codex", false);
    host.files.set("/background.ts", "export const owner = 'background';\n");
    host.setSessions([selected, background]);
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();

    host.publishSession(background.address, "editor", "openFile", {
      path: "/background.ts",
      line: 1,
      preview: false,
      scratch: false,
    });

    await expect(page.locator(".editor-tab", { hasText: "background.ts" })).toHaveCount(0);
    await page.locator('.session-chip[title^="background —"]').click();
    await expect(page.locator(".editor-tab", { hasText: "background.ts" })).toBeVisible();
    await expect(page.locator(".monaco-editor .view-lines").first()).toContainText(
      "owner = 'background'",
    );
  });

  test("removing the selected session selects the remaining live session", async ({ page }) => {
    const main = mockSession("main", "main", "claude", true);
    const feature = mockSession("feature", "feature", "claude", false);
    host.setSessions([main, feature]);
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    await page.locator('.session-chip[title^="feature —"]').click();
    await expect(page.locator(".session-chip.active")).toHaveAttribute("title", /^feature —/);

    host.setSessions([main]);

    await expect(page.locator(".session-chip")).toHaveCount(1);
    await expect(page.locator(".session-chip.active")).toHaveAttribute("title", /^main —/);
  });

  test("a reused slot rejects events from its old incarnation", async ({ page }) => {
    const oldSession = mockSession("same-slot", "old", "codex", true);
    host.setSessions([oldSession]);
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();

    const replacement = {
      ...mockSession("same-slot", "new", "codex", true),
      address: { slot: "same-slot", incarnation: "replacement-incarnation" },
    };
    const checkpoint = host.checkpoint();
    host.setSessions([replacement]);
    await host.waitForSession(replacement.address, "event", "view", "attach", checkpoint);
    host.publishSession(oldSession.address, "agent", "pane", {
      providerId: "codex",
      type: "item-completed",
      itemId: "stale",
      itemType: "agentMessage",
      status: "completed",
      text: "stale incarnation transcript",
    });
    host.publishSession(replacement.address, "agent", "pane", {
      providerId: "codex",
      type: "item-completed",
      itemId: "current",
      itemType: "agentMessage",
      status: "completed",
      text: "replacement incarnation transcript",
    });

    await expect(page.getByText("replacement incarnation transcript")).toBeVisible();
    await expect(page.getByText("stale incarnation transcript")).toHaveCount(0);
  });

  test("an unfocused session completing still plays its attention sound", async ({ page }) => {
    const selected = mockSession("selected", "selected", "codex", true);
    const background = mockSession("background", "background", "codex", false);
    host.setSessions([selected, background]);
    await page.addInitScript(() => {
      (window as unknown as { __attentionSoundPlays: number }).__attentionSoundPlays = 0;
      HTMLMediaElement.prototype.play = function play(): Promise<void> {
        (window as unknown as { __attentionSoundPlays: number }).__attentionSoundPlays += 1;
        return Promise.resolve();
      };
    });
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    await page.locator("body").click({ position: { x: 1, y: 1 } });

    host.publishSession(background.address, "attention", "raised", {
      label: background.label,
      kind: "turnComplete",
    });

    await expect
      .poll(() =>
        page.evaluate(
          () => (window as unknown as { __attentionSoundPlays: number }).__attentionSoundPlays,
        ),
      )
      .toBe(1);
  });

  test("background remote transcripts are retained without replay on selection", async ({
    page,
  }) => {
    const local = mockSession("local", "local", "claude", true);
    const remoteSession = mockSession("remote-codex", "codex", "codex", true);
    host.setSessions([local]);
    const remote = await MockHost.start({ distDir, sessions: [remoteSession] });
    try {
      await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
      await host.waitUntilConnected();
      host.publishHost("remoteAgents", "changed", {
        agents: [{ name: "devbox", url: remote.url, token: "runner-token" }],
      });
      await remote.waitUntilConnected();
      host.publishHost("rail", "changed", {
        lastLocation: "local",
        promoted: ["remote:devbox remote-codex"],
      });
      remote.publishSession(remoteSession.address, "agent", "pane", {
        providerId: "codex",
        type: "item-completed",
        itemId: "answer",
        itemType: "agentMessage",
        status: "completed",
        text: "retained remote transcript",
      });
      await expect(page.getByText("retained remote transcript")).toHaveCount(0);

      await page.locator(".session-chip.remote").click();

      await expect(page.getByText("retained remote transcript")).toBeVisible();
      await expect(page.locator(".session-chip.active")).toHaveAttribute("title", /^codex @/);
    } finally {
      await remote.close();
    }
  });

  test("network status stays degraded until reconnect hello completes", async ({ page }) => {
    const session = mockSession("main", "main", "claude", true);
    host.setSessions([session]);
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    await expect(page.locator(".footer-network-problem")).toHaveCount(0);

    const checkpoint = host.checkpoint();
    host.pauseHello();
    host.disconnectBridge();
    await host.waitUntilConnected(checkpoint);
    await expect(page.locator(".footer-network-problem")).toHaveText("Network Problems");

    host.resumeHello();
    await expect(page.locator(".footer-network-problem")).toHaveCount(0);
  });

  test("replayed terminal device queries stay suppressed while live queries answer", async ({
    page,
  }) => {
    const session = mockSession("main", "main", "claude", true);
    host.setSessions([session]);
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    await host.waitForSession(session.address, "event", "terminal.shell", "ready");

    host.publishSession(session.address, "terminal.shell", "output", {
      dataB64: Buffer.from("AB\x1b[6n").toString("base64"),
      replay: true,
    });
    host.publishSession(session.address, "terminal.shell", "output", {
      dataB64: Buffer.from("WXYZ\x1b[6n").toString("base64"),
      replay: false,
    });

    const input = await host.waitForSession(session.address, "event", "terminal.shell", "input");
    const payload = input.payload as { dataB64: string };
    expect(Buffer.from(payload.dataB64, "base64").toString()).toBe("\x1b[1;7R");
    expect(
      host.received.filter(
        (message) =>
          message.scope === "session" &&
          message.feature === "terminal.shell" &&
          message.name === "input",
      ),
    ).toHaveLength(1);
  });

  test("stays silent in a plain browser with no host advertised", async ({ page }) => {
    const pageErrors: string[] = [];
    page.on("pageerror", (error) => pageErrors.push(error.message));

    await page.goto(`${host.url}/`, { waitUntil: "domcontentloaded" });
    await expect(page.locator(".layout-root")).toBeVisible();

    expect(host.received).toHaveLength(0);
    expect(pageErrors).toEqual([]);
  });
});
