import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test } from "@playwright/test";
import { MockHost } from "./mock-host";

// The built app from `vite build`; the e2e run builds it first (see the `e2e` npm script).
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

    // Outbound: main.tsx posts { type: "ready" } at module load, before the WebSocket opens. It must still
    // arrive — proving the transport buffers pre-open sends and flushes them on connect.
    const ready = await host.waitForMessage("ready");
    expect(ready.type).toBe("ready");

    // Inbound: the host pushes a notify; the app must render it as a toast — proving a WebSocket frame
    // reaches deliverFromHost -> onHostMessage -> App's notify handler. Re-pushed under toPass because App
    // registers its host listener only after mount, so the first push can land before the listener exists.
    const toast = page.locator(".toast-msg", { hasText: "hello-from-mock-host" });
    await expect(async () => {
      host.pushToWeb({ type: "notify", level: "info", message: "hello-from-mock-host" });
      await expect(toast).toBeVisible({ timeout: 1000 });
    }).toPass({ timeout: 20_000 });
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
      const localChip = {
        id: "local-slot",
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
      await remoteChip.click();
      await remoteSwitch;
      remote.pushToWeb({
        type: "set-editor-session",
        sessionId: "remote-owner",
        session: {
          active: "/remote/pixel.png",
          open: [{ path: "/remote/pixel.png", viewState: null }],
        },
      });
      await expect(image).toHaveJSProperty("naturalWidth", 8);

      const localSwitch = host.waitForMessage("switch-session");
      await page.locator(".session-chip:not(.active)").click();
      await localSwitch;
      host.pushToWeb({
        type: "set-editor-session",
        sessionId: "local-owner",
        session: {
          active: "/local/pixel.png",
          open: [{ path: "/local/pixel.png", viewState: null }],
        },
      });
      await expect(image).toHaveJSProperty("naturalWidth", 8);

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
    // The shell renders right after main.tsx posts its module-load `ready` (render() follows that line), so a
    // visible layout-root is the deterministic proof the page booted and had its chance to send — by which point
    // the absent-bridge "none" transport must have swallowed every send. No fixed sleep.
    await expect(page.locator(".layout-root")).toBeVisible();

    expect(host.received).toHaveLength(0);
    expect(pageErrors, `unexpected page errors: ${pageErrors.join("; ")}`).toHaveLength(0);
  });
});
