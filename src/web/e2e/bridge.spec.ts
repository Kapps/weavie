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

  test("client-owned font zoom stays on the local host with a remote session selected", async ({
    page,
  }) => {
    const local = mockSession("local", "local", "codex", true);
    const remoteSession = mockSession("remote-codex", "codex", "codex", true);
    const fontCommand = {
      id: "weavie.font.increase",
      title: "Increase Font Size",
      runsIn: "core",
      owner: "client",
      category: "View",
      description: "Increase the editor and terminal font size by one pixel.",
      aliases: [],
      showInPalette: true,
      keys: ["$mod+="],
    };
    const localCatalog = {
      commands: [fontCommand],
      keybindings: [{ key: "$mod+=", command: fontCommand.id }],
    };
    host.setSessions([local]);
    const remote = await MockHost.start({
      distDir,
      sessions: [remoteSession],
      commandCatalog: {
        commands: [{ ...fontCommand, owner: "backend", title: "Remote duplicate" }],
        keybindings: [{ key: "$mod+=", command: fontCommand.id }],
      },
    });
    try {
      await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
      await host.waitUntilConnected();
      host.publishHost("commands", "catalog", localCatalog);
      host.publishHost("remoteAgents", "changed", {
        agents: [{ name: "devbox", url: remote.url, token: "runner-token" }],
      });
      await remote.waitUntilConnected();
      host.publishHost("rail", "changed", {
        lastLocation: "local",
        promoted: ["remote:devbox remote-codex"],
      });
      await page.locator(".session-chip.remote").click();
      await expect(page.locator(".session-chip.active")).toHaveAttribute("title", /^codex @/);

      const offLocal = host.onSession(local.address, "request", "commands", "invoke", (message) => {
        host.respond(message, { ok: true, message: null, error: null });
        host.publishHost("settings", "fonts", {
          editor: { family: "monospace", size: 19, weight: "normal" },
          terminal: { family: "monospace", size: 19, weight: "normal" },
        });
      });
      const remoteCheckpoint = remote.checkpoint();
      const request = host.waitForSession(local.address, "request", "commands", "invoke");

      await page.keyboard.press("ControlOrMeta+=");

      expect((await request).payload).toMatchObject({ id: fontCommand.id });
      await expect
        .poll(() =>
          page
            .locator(".agent-body:visible")
            .first()
            .evaluate((element) => getComputedStyle(element).fontSize),
        )
        .toBe("19px");

      await page.locator(".session-chip:not(.remote)").click();
      const localCheckpoint = host.checkpoint();
      const relayedInvocation = host.waitForSession(
        local.address,
        "request",
        "commands",
        "invoke",
        localCheckpoint,
      );
      const clientResponse = remote.requestSession(remoteSession.address, "commands", "runClient", {
        id: fontCommand.id,
        args: null,
      });
      expect((await relayedInvocation).payload).toMatchObject({ id: fontCommand.id });
      expect((await clientResponse).payload).toMatchObject({ ok: true });
      expect(
        remote.received
          .slice(remoteCheckpoint)
          .filter(
            (message) =>
              message.kind === "request" &&
              message.feature === "commands" &&
              message.name === "invoke",
          ),
      ).toEqual([]);
      offLocal();
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
    await expect(page.locator(".toast-msg")).toHaveText(
      "Lost connection to the Weavie host. Reconnecting…",
    );

    host.resumeHello();
    await expect(page.locator(".footer-network-problem")).toHaveCount(0);
    await expect(page.locator(".toast-msg")).toHaveCount(0);
  });

  test("mobile inbox stays usable and truthful through a long catalog and reconnect", async ({
    page,
  }) => {
    const primary = mockSession("main", "main", "codex", true);
    const dormant = Array.from({ length: 12 }, (_, index) => ({
      ...mockSession(`dormant-${index}`, `feature/dormant-${index}`, "codex", false),
      address: null,
      loaded: false,
    }));
    host.setSessions([primary, ...dormant]);
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();

    const inbox = page.locator(".session-inbox");
    const geometry = await inbox.evaluate((element) => {
      const composer = element.querySelector<HTMLElement>(".session-composer");
      const options = element.querySelector<HTMLElement>(".session-composer-options");
      const optionButton = options?.querySelector("button") ?? null;
      const list = element.querySelector<HTMLElement>(".session-inbox-list");
      if (composer === null || options === null || optionButton === null || list === null) {
        throw new Error("mobile session inbox is incomplete");
      }
      return {
        composerBottom: composer.getBoundingClientRect().bottom,
        optionsBottom: options.getBoundingClientRect().bottom,
        optionTargetHeights: [...options.querySelectorAll("select, button")].map(
          (target) => target.getBoundingClientRect().height,
        ),
        optionTargetFontSize: getComputedStyle(optionButton).fontSize,
        optionTargetRadius: getComputedStyle(optionButton).borderRadius,
        optionTargetRows: new Set(
          [...options.querySelectorAll("select, button")].map((target) =>
            Math.round(target.getBoundingClientRect().top),
          ),
        ).size,
        listClientHeight: list.clientHeight,
        listScrollHeight: list.scrollHeight,
      };
    });
    expect(geometry.optionsBottom).toBeLessThanOrEqual(geometry.composerBottom + 1);
    expect(Math.min(...geometry.optionTargetHeights)).toBeGreaterThanOrEqual(44);
    expect(geometry.optionTargetRows).toBe(2);
    expect(geometry.listScrollHeight).toBeGreaterThan(geometry.listClientHeight);

    const unloaded = inbox.locator(".session-inbox-row", { hasText: "Unloaded" });
    await expect(unloaded).toHaveCount(dormant.length);
    await expect(unloaded.locator(".session-status")).toHaveCount(0);

    const draft = inbox.getByRole("textbox", { name: "Prompt for a new session" });
    await draft.fill("Keep this mobile draft");
    const agentTab = page.getByRole("button", { name: "Agent", exact: true });
    await agentTab.click();
    await page.getByRole("button", { name: "Sessions", exact: true }).click();
    await expect(draft).toHaveValue("Keep this mobile draft");
    await agentTab.click();
    await expect(agentTab).toBeFocused();
    await page.getByRole("button", { name: "Sessions", exact: true }).click();

    const checkpoint = host.checkpoint();
    host.pauseHello();
    host.disconnectBridge();
    await host.waitUntilConnected(checkpoint);
    const primaryRow = inbox.locator(".session-inbox-row").first();
    await expect(primaryRow.locator(".session-inbox-state")).toHaveText("Reconnecting");
    const statusColors = await primaryRow.locator(".session-status").evaluate((status) => {
      const probe = document.createElement("span");
      probe.style.background = "var(--dim)";
      document.body.append(probe);
      const dim = getComputedStyle(probe).backgroundColor;
      probe.remove();
      return { dot: getComputedStyle(status).backgroundColor, dim };
    });
    expect(statusColors.dot).toBe(statusColors.dim);

    const toast = page.locator(".toast", { hasText: "Lost connection" });
    await expect(toast).toBeVisible();
    const overlay = await page.evaluate(() => {
      const composer = document.querySelector(".session-composer");
      const visibleToast = document.querySelector(".toast");
      const toastClose = document.querySelector(".toast-close");
      const surfaceBar = document.querySelector(".mobile-surface-bar");
      if (
        composer === null ||
        visibleToast === null ||
        toastClose === null ||
        surfaceBar === null
      ) {
        throw new Error("mobile overlay geometry is incomplete");
      }
      return {
        composerBottom: composer.getBoundingClientRect().bottom,
        toastTop: visibleToast.getBoundingClientRect().top,
        toastBottom: visibleToast.getBoundingClientRect().bottom,
        toastClose: toastClose.getBoundingClientRect().toJSON(),
        surfaceBarTop: surfaceBar.getBoundingClientRect().top,
      };
    });
    expect(overlay.toastTop).toBeGreaterThanOrEqual(overlay.composerBottom);
    expect(overlay.toastBottom).toBeLessThanOrEqual(overlay.surfaceBarTop);
    expect(overlay.toastClose.width).toBeGreaterThanOrEqual(44);
    expect(overlay.toastClose.height).toBeGreaterThanOrEqual(44);

    host.resumeHello();
    await expect(primaryRow.locator(".session-inbox-state")).toHaveText("Idle");
    await expect(toast).toHaveCount(0);

    await inbox.getByRole("button", { name: "More…" }).click();
    const promptTargets = page.locator(
      ".session-prompt-input, .session-prompt-select, .session-prompt-location-remove",
    );
    await expect(promptTargets).not.toHaveCount(0);
    expect(
      Math.min(
        ...(await promptTargets.evaluateAll((targets) =>
          targets.map((target) => target.getBoundingClientRect().height),
        )),
      ),
    ).toBeGreaterThanOrEqual(44);
    await page.getByRole("button", { name: "Cancel" }).click();

    await page.getByRole("button", { name: "Agent" }).click();
    const agentComposer = page.locator("[data-agent-composer]");
    await expect(agentComposer).toBeVisible();
    host.publishSession(primary.address, "agent", "pane", {
      providerId: "codex",
      type: "turn-started",
      turnId: "turn-mobile-layout",
      status: "inProgress",
    });
    await expect(agentComposer.getByRole("button", { name: "Interrupt" })).toBeVisible();
    await expect(agentComposer.getByRole("button", { name: "Steer" })).toBeVisible();
    const composerGeometry = await agentComposer.evaluate((composer) => {
      const textarea = composer.querySelector("textarea");
      const actions = composer.querySelector(".agent-compose-actions");
      const actionButton = actions?.querySelector("button") ?? null;
      if (textarea === null || actions === null || actionButton === null) {
        throw new Error("mobile agent composer is incomplete");
      }
      return {
        actionsTop: actions.getBoundingClientRect().top,
        buttonHeights: [...actions.querySelectorAll("button")].map(
          (button) => button.getBoundingClientRect().height,
        ),
        buttonFontSize: getComputedStyle(actionButton).fontSize,
        buttonRadius: getComputedStyle(actionButton).borderRadius,
        textareaHeight: textarea.getBoundingClientRect().height,
        textareaBottom: textarea.getBoundingClientRect().bottom,
      };
    });
    expect(composerGeometry.actionsTop).toBeGreaterThanOrEqual(composerGeometry.textareaBottom);
    expect(Math.min(...composerGeometry.buttonHeights)).toBeGreaterThanOrEqual(44);
    expect(composerGeometry.buttonFontSize).toBe(geometry.optionTargetFontSize);
    expect(composerGeometry.buttonRadius).toBe(geometry.optionTargetRadius);
    expect(composerGeometry.textareaHeight).toBeGreaterThanOrEqual(44);
    host.publishHost("notifications", "show", {
      level: "error",
      message: "Agent surface error",
    });
    const agentToast = page.locator(".toast", { hasText: "Agent surface error" });
    await expect(agentToast).toBeVisible();
    const agentOverlay = await Promise.all([
      agentToast.evaluate((element) => element.getBoundingClientRect().bottom),
      agentComposer.evaluate((element) => element.getBoundingClientRect().top),
    ]);
    expect(agentOverlay[0]).toBeLessThanOrEqual(agentOverlay[1]);
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
