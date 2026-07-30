import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, type Page, test } from "@playwright/test";
import { MockHost, mockSessionChip as sessionChip } from "./mock-host";

// The built app from `vite build`; the e2e run builds it first (see the `e2e` npm script).
const distDir = join(dirname(fileURLToPath(import.meta.url)), "..", "dist");

test.beforeAll(() => {
  if (!existsSync(join(distDir, "index.html"))) {
    throw new Error(
      `built app not found at ${distDir}; run \`pnpm run build\` before the e2e tests`,
    );
  }
});

async function showCustomTitleBar(page: Page): Promise<void> {
  await page.addInitScript(() => {
    window.__WEAVIE_SHELL__ = {
      platform: "win",
      titleBar: "custom",
      workspaceLabel: "test",
      recents: [],
      buildNumber: "test",
    };
  });
}

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

    // Outbound: main.tsx posts { type: "ready" } after mounting App, usually before the WebSocket opens. It
    // must still arrive — proving the transport buffers pre-open sends and flushes them on connect.
    const ready = await host.waitForMessage("ready");
    expect(ready.type).toBe("ready");

    // Inbound: the host pushes a notify; the app must render it as a toast — proving a WebSocket frame
    // reaches deliverFromHost -> onHostMessage -> App's notify handler.
    const toast = page.locator(".toast-msg", { hasText: "hello-from-mock-host" });
    host.pushToWeb({ type: "notify", level: "info", message: "hello-from-mock-host" });
    await expect(toast).toBeVisible();
  });

  test("live fonts update normal DOM and source-shadow typography roles", async ({ page }) => {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitForMessage("ready");

    host.pushToWeb({
      type: "source-loading",
      target: "typography-source",
      title: "Typography",
      sourceId: "notion",
    });
    host.pushToWeb({
      type: "source-doc",
      target: "typography-source",
      title: "Typography",
      sourceId: "notion",
      markdown: "Body with `code`.",
      editedTime: "",
    });
    host.pushToWeb({ type: "prompt-source-token", sourceId: "notion", label: "Notion" });

    const prose = page.locator(".editor-source .wv-source");
    const sourceCode = prose.locator("code");
    const promptInput = page.locator(".session-prompt-input");
    await expect(sourceCode).toBeVisible();
    await expect(promptInput).toBeVisible();

    host.pushToWeb({
      type: "fonts",
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

  test("a same-backend session switch reuses the live agent pane", async ({ page }) => {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitForMessage("ready");
    host.pushToWeb({
      type: "session-list",
      sessions: [
        sessionChip("local-main", "main", "codex", true, true),
        sessionChip("local-feature", "feature", "codex", false, false),
      ],
    });

    const switched = host.waitForMessage("switch-session");
    await page.locator('.session-chip[title^="feature —"]').click();

    expect(await switched).toMatchObject({
      id: "local-feature",
      replayAgentState: false,
    });
  });

  test("Go to File hides the outgoing index and restores its cache when a switch is rejected", async ({
    page,
  }) => {
    await showCustomTitleBar(page);
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitForMessage("ready");
    const main = sessionChip("local-main", "main", "claude", true, true);
    const feature = sessionChip("local-feature", "feature", "claude", false, false);
    host.pushToWeb({ type: "session-list", sessions: [main, feature] });
    host.pushToWeb({
      type: "set-editor-session",
      sessionId: "main-owner",
      railSessionId: main.id,
      session: { active: null, open: [] },
    });
    host.pushToWeb({
      type: "file-index",
      root: "/main",
      files: ["/main/original.ts"],
      railSessionId: main.id,
    });

    const input = page.locator(".tb-omnibar-input");
    await input.click();
    await input.fill("original");
    await expect(page.locator(".tb-omnibar-row", { hasText: "original.ts" })).toBeVisible();
    await page.keyboard.press("Escape");

    await page.locator('.session-chip[title^="feature —"]').click();
    await input.click();
    await input.fill("latest");
    await expect(page.locator(".tb-omnibar-empty")).toHaveText("Loading files…");

    // The mounted session may refresh its hidden cache, but the pending target cannot expose it.
    host.pushToWeb({
      type: "file-index",
      root: "/main",
      files: ["/main/latest.ts"],
      railSessionId: main.id,
    });
    await expect(page.locator(".tb-omnibar-row", { hasText: "latest.ts" })).toHaveCount(0);
    await expect(page.locator(".tb-omnibar-empty")).toHaveText("Loading files…");

    host.pushToWeb({ type: "session-list", sessions: [main] });
    await expect(page.locator(".tb-omnibar-row", { hasText: "latest.ts" })).toBeVisible();
    await expect(page.locator(".tb-omnibar-row", { hasText: "original.ts" })).toHaveCount(0);
  });

  test("a restored session never adopts an unowned index before its first host push", async ({
    page,
  }) => {
    await showCustomTitleBar(page);
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitForMessage("ready");
    const main = sessionChip("local-main", "main", "claude", false, true);
    const restored = sessionChip("local-restored", "restored", "claude", true, false);
    const other = sessionChip("local-other", "other", "claude", false, false);
    host.pushToWeb({ type: "session-list", sessions: [main, restored, other] });
    host.pushToWeb({
      type: "set-editor-session",
      sessionId: "restored-owner",
      railSessionId: restored.id,
      session: { active: null, open: [] },
    });

    const input = page.locator(".tb-omnibar-input");
    await input.click();
    await input.fill("restored");
    await expect(page.locator(".tb-omnibar-empty")).toHaveText("Loading files…");

    await page.locator('.session-chip[title^="other —"]').click();
    host.pushToWeb({ type: "session-list", sessions: [main, restored] });
    await input.click();
    await input.fill("restored");
    await expect(page.locator(".tb-omnibar-empty")).toHaveText("Loading files…");
  });

  test("a rapid switch retains the mounted index if the latest target is rejected", async ({
    page,
  }) => {
    await showCustomTitleBar(page);
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitForMessage("ready");
    const main = sessionChip("local-main", "main", "claude", true, true);
    const first = sessionChip("local-first", "first", "claude", false, false);
    const second = sessionChip("local-second", "second", "claude", false, false);
    host.pushToWeb({ type: "session-list", sessions: [main, first, second] });
    host.pushToWeb({
      type: "set-editor-session",
      sessionId: "main-owner",
      railSessionId: main.id,
      session: { active: null, open: [] },
    });
    host.pushToWeb({
      type: "file-index",
      root: "/main",
      files: ["/main/main.ts"],
      railSessionId: main.id,
    });

    await page.locator('.session-chip[title^="first —"]').click();
    await page.locator('.session-chip[title^="second —"]').click();
    const input = page.locator(".tb-omnibar-input");
    await input.click();
    await input.fill("second");
    await expect(page.locator(".tb-omnibar-empty")).toHaveText("Loading files…");

    host.pushToWeb({
      type: "set-editor-session",
      sessionId: "first-owner",
      railSessionId: first.id,
      session: { active: null, open: [] },
    });
    host.pushToWeb({
      type: "session-list",
      sessions: [{ ...main, active: false }, { ...first, active: true }, second],
    });
    host.pushToWeb({
      type: "file-index",
      root: "/first",
      files: ["/first/first.ts"],
      railSessionId: first.id,
    });
    await expect(page.locator(".tb-omnibar-empty")).toHaveText("Loading files…");

    // The latest target disappears after the intermediate target mounted. Its cached index becomes visible.
    host.pushToWeb({
      type: "session-list",
      sessions: [
        { ...main, active: false },
        { ...first, active: true },
      ],
    });
    await input.fill("first");
    await expect(page.locator(".tb-omnibar-row", { hasText: "first.ts" })).toBeVisible();
    await expect(page.locator(".tb-omnibar-row", { hasText: "main.ts" })).toHaveCount(0);

    // Retrying the latest target replaces that cache; an older host's unstamped final frame remains compatible.
    host.pushToWeb({
      type: "session-list",
      sessions: [{ ...main, active: false }, { ...first, active: true }, second],
    });
    await page.locator('.session-chip[title^="second —"]').click();
    await input.click();
    await input.fill("second");
    await expect(page.locator(".tb-omnibar-empty")).toHaveText("Loading files…");
    host.pushToWeb({
      type: "set-editor-session",
      sessionId: "second-owner",
      railSessionId: second.id,
      session: { active: null, open: [] },
    });
    host.pushToWeb({
      type: "session-list",
      sessions: [{ ...main, active: false }, first, { ...second, active: true }],
    });
    host.pushToWeb({
      type: "file-index",
      root: "/second",
      files: [],
      pending: true,
      railSessionId: second.id,
    });
    await expect(page.locator(".tb-omnibar-empty")).toHaveText("Loading files…");
    // An older additive-protocol host omits railSessionId; its FIFO frame belongs to the mounted projection.
    host.pushToWeb({ type: "file-index", root: "/second", files: ["/second/second.ts"] });
    await expect(page.locator(".tb-omnibar-row", { hasText: "second.ts" })).toBeVisible();
    await expect(page.locator(".tb-omnibar-row", { hasText: "first.ts" })).toHaveCount(0);
  });

  test("a removed switch target cannot leave the rail optimistically highlighted", async ({
    page,
  }) => {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitForMessage("ready");
    const main = sessionChip("local-main", "main", "claude", true, true);
    const feature = sessionChip("local-feature", "feature", "claude", false, false);
    host.pushToWeb({ type: "session-list", sessions: [main, feature] });
    host.pushToWeb({
      type: "set-editor-session",
      sessionId: "main-owner",
      railSessionId: main.id,
      session: { active: null, open: [] },
    });
    await host.waitForMessage("editor-projection-mounted");
    const unsubscribe = host.onReceived("switch-session", () => {
      host.pushToWeb({ type: "session-list", sessions: [main] });
    });

    try {
      await page.locator('.session-chip[title^="feature —"]').click();

      await expect(page.locator(".session-chip")).toHaveCount(1);
      await expect(page.locator(".session-chip.active")).toHaveAttribute("title", /^main —/);
    } finally {
      unsubscribe();
    }
  });

  test("Ctrl+Tab projects the rail target but commits the backend only with its editor projection", async ({
    page,
  }) => {
    const remote = await MockHost.start({ distDir });
    try {
      await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
      await host.waitForMessage("ready");
      host.pushToWeb({
        type: "session-list",
        sessions: [sessionChip("ma", "MA", "claude", true, true)],
      });
      host.pushToWeb({
        type: "set-editor-session",
        sessionId: "ma",
        session: { active: null, open: [] },
      });
      host.pushToWeb({
        type: "remote-agents",
        agents: [{ name: "devbox", url: remote.url, token: "runner-token" }],
      });
      await remote.waitForMessage("ready");
      remote.pushToWeb({
        type: "session-list",
        sessions: [
          sessionChip("fa", "FA", "claude", false, false),
          sessionChip("td", "TD", "claude", false, false),
          sessionChip("rt", "RT", "claude", true, false),
        ],
      });
      host.pushToWeb({
        type: "rail-state",
        lastLocation: "local",
        promoted: ["remote:devbox fa", "remote:devbox td", "remote:devbox rt"],
      });
      host.pushToWeb({
        type: "commands",
        commands: [
          {
            id: "weavie.session.next",
            title: "Next Session",
            runsIn: "web",
            description: "Switch to the next session on the rail.",
            aliases: [],
            showInPalette: true,
            keys: ["ctrl+Tab"],
          },
        ],
        keybindings: [{ key: "ctrl+Tab", command: "weavie.session.next" }],
      });
      await expect(page.locator(".session-chip")).toHaveCount(4);

      const switched = remote.waitForMessage("switch-session");
      await page.evaluate(() =>
        window.dispatchEvent(
          new KeyboardEvent("keydown", {
            key: "Tab",
            ctrlKey: true,
            bubbles: true,
            cancelable: true,
          }),
        ),
      );

      expect(await switched).toMatchObject({ id: "fa", replayAgentState: true });
      await expect(page.locator(".session-chip.active")).toHaveAttribute("title", /^FA @/);
      remote.pushToWeb({
        type: "set-editor-session",
        sessionId: "fa",
        session: { active: null, open: [] },
      });
      await expect(page.locator(".session-chip.active")).toHaveAttribute("title", /^FA @/);
    } finally {
      await remote.close();
    }
  });

  test("a backend switch never mixes the incoming resource host with the outgoing media identity", async ({
    page,
  }) => {
    const remote = await MockHost.start({ distDir });
    const pixel = Buffer.from(
      "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAEklEQVR4nGP4z8CAFWEXHbQSACj/P8Fu7N9hAAAAAElFTkSuQmCC",
      "base64",
    );
    host.setMedia("local-owner", "/local/pixel.png", pixel);
    remote.setMedia("remote-owner", "/remote/pixel.png", pixel);

    try {
      await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
      await host.waitForMessage("ready");
      const localChip = sessionChip("local-slot", "main", "claude", true, true);
      await expect(async () => {
        host.pushToWeb({ type: "session-list", sessions: [localChip] });
        await expect
          .poll(() => host.received.some((message) => message.type === "term-ready"), {
            timeout: 1000,
          })
          .toBe(true);
      }).toPass({ timeout: 20_000 });
      host.pushToWeb({
        type: "set-editor-session",
        sessionId: "local-owner",
        railSessionId: "local-slot",
        session: {
          active: "/local/pixel.png",
          open: [{ path: "/local/pixel.png", viewState: null }],
        },
      });
      const image = page.locator(".editor-media img");
      await expect(image).toHaveJSProperty("naturalWidth", 8);

      host.pushToWeb({
        type: "remote-agents",
        agents: [{ name: "devbox", url: remote.url, token: "runner-token" }],
      });
      await remote.waitForMessage("ready");
      remote.pushToWeb({
        type: "session-list",
        sessions: [{ ...localChip, id: "remote-slot", label: "remote", primary: false }],
      });
      host.pushToWeb({
        type: "rail-state",
        lastLocation: "local",
        promoted: ["remote:devbox remote-slot"],
      });
      const remoteChip = page.locator(".session-chip.remote");
      await expect(remoteChip).toBeVisible();

      const remoteSwitch = remote.waitForMessage("switch-session");
      const localRelease = host.waitForMessage("release-editor");
      await remoteChip.click();
      expect(await remoteSwitch).toMatchObject({ replayAgentState: true });
      expect(await localRelease).toMatchObject({ sessionId: "local-owner" });
      remote.pushToWeb({
        type: "set-editor-session",
        sessionId: "remote-owner",
        railSessionId: "remote-slot",
        session: {
          active: "/remote/pixel.png",
          open: [{ path: "/remote/pixel.png", viewState: null }],
        },
      });
      await expect(image).toHaveJSProperty("naturalWidth", 8);
      await expect(page.locator(".session-chip.active")).toHaveAttribute("title", /^remote @/);

      const localSwitch = host.waitForMessage("switch-session");
      const remoteRelease = remote.waitForMessage("release-editor");
      await page.locator('.session-chip[title^="main —"]').click();
      expect(await localSwitch).toMatchObject({ replayAgentState: true });
      expect(await remoteRelease).toMatchObject({ sessionId: "remote-owner" });
      host.pushToWeb({
        type: "set-editor-session",
        sessionId: "local-owner",
        railSessionId: "local-slot",
        session: {
          active: "/local/pixel.png",
          open: [{ path: "/local/pixel.png", viewState: null }],
        },
      });
      await expect(image).toHaveJSProperty("naturalWidth", 8);
      await expect(page.locator(".session-chip.active")).toHaveAttribute("title", /^main —/);

      expect(
        [...host.mediaRequests, ...remote.mediaRequests].filter((request) => request.status >= 400),
      ).toEqual([]);
      expect(host.mediaRequests).toContainEqual({
        session: "local-owner",
        path: "/local/pixel.png",
        status: 200,
      });
      expect(remote.mediaRequests).toContainEqual({
        session: "remote-owner",
        path: "/remote/pixel.png",
        status: 200,
      });
    } finally {
      await remote.close();
    }
  });

  test("selecting a background remote Codex session replays the transcript hidden at connect", async ({
    page,
  }) => {
    const remote = await MockHost.start({ distDir });
    try {
      await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
      await host.waitForMessage("ready");
      host.pushToWeb({
        type: "session-list",
        sessions: [sessionChip("local-slot", "main", "claude", true, true)],
      });
      host.pushToWeb({
        type: "remote-agents",
        agents: [{ name: "devbox", url: remote.url, token: "runner-token" }],
      });
      await remote.waitForMessage("ready");
      remote.pushToWeb({
        type: "session-list",
        sessions: [sessionChip("remote-codex", "codex", "codex", true, false)],
      });
      host.pushToWeb({
        type: "rail-state",
        lastLocation: "local",
        promoted: ["remote:devbox remote-codex"],
      });

      const transcript = {
        type: "item-completed",
        providerId: "codex",
        itemId: "answer",
        itemType: "agentMessage",
        status: "completed",
        text: "retained remote transcript",
      };
      remote.pushToWeb({
        type: "agent-pane-reset",
        slot: "remote-codex",
        workspace: "/remote/repo",
      });
      remote.pushToWeb({
        type: "agent-pane-batch",
        slot: "remote-codex",
        workspace: "/remote/repo",
        messages: [transcript],
      });
      await expect(page.getByText(transcript.text)).toHaveCount(0);

      const remoteChip = page.locator(".session-chip.remote");
      await expect(remoteChip).toBeVisible();
      const switched = remote.waitForMessage("switch-session");
      await remoteChip.click();
      expect(await switched).toMatchObject({ replayAgentState: true });

      remote.pushToWeb({
        type: "set-editor-session",
        sessionId: "remote-codex",
        session: { active: null, open: [] },
      });

      // HostCore.SwitchToSlot now sends this authoritative replay after the web has admitted the backend.
      remote.pushToWeb({
        type: "agent-pane-reset",
        slot: "remote-codex",
        workspace: "/remote/repo",
      });
      remote.pushToWeb({
        type: "agent-pane-batch",
        slot: "remote-codex",
        workspace: "/remote/repo",
        messages: [transcript],
      });
      await expect(page.getByText(transcript.text)).toBeVisible();
    } finally {
      await remote.close();
    }
  });

  test("the status bar reports network problems until reconnect replay completes", async ({
    page,
  }) => {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitForMessage("ready");
    await expect(page.locator(".connection-banner")).toHaveCount(0);

    host.pauseBridgeReady();
    host.disconnectBridge();

    await expect(page.locator(".footer-network-problem")).toHaveText("Network Problems");
    await expect
      .poll(() => host.received.filter((message) => message.type === "ready").length)
      .toBeGreaterThanOrEqual(2);
    await expect(page.locator(".footer-network-problem")).toBeVisible();

    host.resumeBridgeReady();
    await expect(page.locator(".footer-network-problem")).toHaveCount(0);
  });

  test("a legacy worker clears connecting state without a bridge-ready marker", async ({
    page,
  }) => {
    await host.close();
    host = await MockHost.start({ distDir, readyReplayProtocol: 0 });

    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitForMessage("ready");

    await expect(page.locator(".connection-banner")).toHaveCount(0);
    await expect(page.locator(".footer-network-problem")).toHaveCount(0);
  });

  test("a replayed device query goes unanswered; the same query live is answered", async ({
    page,
  }) => {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitForMessage("ready");

    // Mount a session's panes. Re-pushed under toPass because App registers its host listener only after
    // mount; the pane's term-ready is the proof the xterm is live and bound to this slot.
    const chip = {
      id: "s1",
      label: "main",
      active: true,
      loaded: true,
      primary: true,
      providerId: "claude",
      status: "idle",
      hue: 200,
      monogram: "M",
    };
    await expect(async () => {
      host.pushToWeb({ type: "session-list", sessions: [chip] });
      await expect
        .poll(() => host.received.some((m) => m.type === "term-ready" && m.session === "shell"), {
          timeout: 1000,
        })
        .toBe(true);
    }).toPass({ timeout: 20_000 });

    // A cursor-position query (ESC[6n) inside a replay-flagged chunk — scrollback a reattach replays — was
    // already answered in the pane's previous life; xterm's re-answer must not reach the host as term-input
    // (it would hit the child's stdin and echo as ^[[19;23R at the prompt). Each chunk prints a different-width
    // prefix before its query, so the two possible CPR replies differ: writes parse in order, so the FIRST
    // term-input being the live chunk's reply (col 7) proves the replay's (col 3) was suppressed — no timing.
    host.pushToWeb({
      type: "term-output",
      slot: "s1",
      session: "shell",
      dataB64: Buffer.from("AB\x1b[6n").toString("base64"),
      replay: true,
    });
    // The same query arriving live IS answered.
    host.pushToWeb({
      type: "term-output",
      slot: "s1",
      session: "shell",
      dataB64: Buffer.from("WXYZ\x1b[6n").toString("base64"),
    });

    const input = await host.waitForMessage("term-input");
    const reply = Buffer.from(String(input.dataB64), "base64").toString();
    expect(reply).toBe("\x1b[1;7R"); // after "AB" + "WXYZ" the cursor sits at row 1, col 7
    expect(host.received.filter((m) => m.type === "term-input")).toHaveLength(1);
  });

  test("the bridge stays silent in a plain browser with no host advertised", async ({ page }) => {
    // No `?weavie-bridge=` and no injected bridge global: the transport must resolve to "none" — the page
    // boots, posts nothing over the absent bridge, and never throws. Guards the no-bridge path.
    const pageErrors: string[] = [];
    page.on("pageerror", (error) => pageErrors.push(error.message));

    await page.goto(`${host.url}/`, { waitUntil: "domcontentloaded" });
    // The shell renders immediately before main.tsx posts `ready`, so a visible layout-root is the deterministic
    // proof the page booted and had its chance to send — by which point the absent-bridge "none" transport must
    // have swallowed every send. No fixed sleep.
    await expect(page.locator(".layout-root")).toBeVisible();

    expect(host.received).toHaveLength(0);
    expect(pageErrors, `unexpected page errors: ${pageErrors.join("; ")}`).toHaveLength(0);
  });
});
