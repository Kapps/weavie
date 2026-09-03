import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test } from "@playwright/test";
import { CommandIds } from "../src/commands/types";
import { MockHost, mockSession } from "./mock-host";

const distDir = join(dirname(fileURLToPath(import.meta.url)), "..", "dist");
test.beforeAll(() => {
  if (!existsSync(join(distDir, "index.html"))) {
    throw new Error(
      `built app not found at ${distDir}; run \`pnpm run build\` before the e2e tests`,
    );
  }
});

const BRANCH_INFERENCE_DRAFT =
  "branch inference fails for this desktop draft and the composer has to ask the user to name it";

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
      payload: {},
    });

    host.publishHost("notifications", "show", {
      level: "info",
      message: "hello-from-mock-host",
    });
    await expect(page.locator(".toast-msg", { hasText: "hello-from-mock-host" })).toBeVisible();
  });

  test("installs and removes an explicitly selected ACP registry distribution", async ({
    page,
  }) => {
    let installed = false;
    host.onHost("request", "git", "branches", (request) => host.respond(request, ["main"]));
    host.onHost("request", "acpRegistry", "list", (request) =>
      host.respond(request, [
        {
          id: "sample",
          name: "Sample ACP",
          version: "1.2.3",
          description: "A registry agent",
          distributions: ["npx", "uvx"],
          installedDistribution: installed ? "uvx" : null,
          installedVersion: installed ? "1.2.3" : null,
        },
      ]),
    );
    host.onHost("request", "acpRegistry", "install", (request) => {
      expect(request.payload).toEqual({ id: "sample", distribution: "uvx" });
      installed = true;
      host.respond(request, null);
    });
    host.onHost("request", "acpRegistry", "remove", (request) => {
      expect(request.payload).toEqual({ id: "sample" });
      installed = false;
      host.respond(request, null);
    });

    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    await page.locator(".session-rail-add").click();
    await page.getByRole("button", { name: "Manage ACP agents" }).click();
    const dialog = page.getByRole("dialog", { name: "ACP agents" });
    await expect(dialog).toBeVisible();
    await dialog.getByRole("combobox", { name: "Distribution for Sample ACP" }).selectOption("uvx");
    await dialog.getByRole("button", { name: "Install" }).click();
    await expect(dialog.getByText("Installed", { exact: true })).toBeVisible();
    await dialog.getByRole("button", { name: "Remove" }).click();
    await expect(dialog.getByRole("button", { name: "Install" })).toBeVisible();
  });

  test("desktop Sessions modal preserves the active session and requires manual naming after inference fails", async ({
    page,
  }) => {
    const session = mockSession("main", "main", "acp");
    const branches = ["main"];
    host.onHost("request", "git", "branches", (request) => host.respond(request, branches));
    host.setSessions([session]);
    await page.setViewportSize({ width: 1200, height: 800 });
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    await expect(page.locator(".session-chip.active")).toHaveCount(1);
    host.publishHost("commands", "catalog", {
      commands: [
        {
          id: CommandIds.newSession,
          title: "New Session",
          runsIn: "core",
          owner: "backend",
          executionLane: "weavie.session.lifecycle",
          scope: "host",
          description: "Start or open a session.",
          aliases: [],
          showInPalette: true,
          keys: [],
        } satisfies CommandInfo,
      ],
      keybindings: [],
    });

    const inbox = page.locator(".session-inbox");
    const modal = page.getByRole("dialog", { name: "Sessions" });
    const add = page.locator(".session-rail-add");
    const activeChip = page.locator(".session-chip.active");
    await expect(inbox).toBeHidden();
    await expect(page.locator(".layout-root")).toBeVisible();
    await expect(activeChip).toHaveCount(1);

    host.publishSession(session.address, "sources", "promptToken", {
      sourceId: "notion",
      label: "Notion",
    });
    const sourceModal = page.getByRole("dialog", { name: "Connect Notion" });
    await expect(sourceModal).toBeVisible();
    await add.evaluate((button) => button.click());
    await expect(sourceModal).toBeVisible();
    await expect(modal).toHaveCount(0);
    expect(
      host.received.filter(
        (message) => message.feature === "sources" && message.name === "dismissToken",
      ),
    ).toHaveLength(0);
    await page.keyboard.press("Escape");
    await expect(sourceModal).toBeHidden();
    await expect(modal).toBeVisible();
    expect(
      host.received.filter(
        (message) => message.feature === "sources" && message.name === "dismissToken",
      ),
    ).toHaveLength(1);
    await page.keyboard.press("Escape");
    await expect(modal).toBeHidden();

    await add.click();
    await expect(modal).toBeVisible();
    host.publishSession(session.address, "sources", "promptToken", {
      sourceId: "notion",
      label: "Notion",
    });
    await expect(sourceModal).toHaveCount(0);
    await page.keyboard.press("Escape");
    await expect(modal).toBeHidden();
    await expect(sourceModal).toBeVisible();
    await expect(sourceModal.locator(".session-prompt-input")).toBeFocused();
    await expect(add).not.toBeFocused();
    await page.keyboard.press("Escape");
    await expect(sourceModal).toBeHidden();

    await activeChip.click({ button: "right" });
    await expect(page.locator(".context-menu")).toBeVisible();
    await add.evaluate((button) => button.click());
    await expect(modal).toBeVisible();
    await expect(page.locator(".context-menu")).toBeHidden();
    await page.keyboard.press("Escape");
    await expect(modal).toBeHidden();

    await add.click();
    await expect(modal).toBeVisible();
    await expect(inbox.getByRole("heading", { name: "Sessions" })).toBeVisible();
    await expect(inbox.getByRole("heading", { name: "Start a new session" })).toBeVisible();
    const openGroup = inbox.getByRole("region", { name: "Open an existing branch" });
    await expect(openGroup).toBeVisible();
    await expect(openGroup.locator("textarea")).toHaveCount(0);
    await expect(page.locator(".layout-root")).toBeVisible();
    await expect(page.locator(".session-prompt-overlay")).toHaveCount(0);
    await expect(activeChip).toHaveCount(1);
    const draft = inbox.getByRole("textbox", { name: "Prompt for a new session" });
    const close = modal.getByRole("button", { name: "Close Sessions" });
    await expect(draft).toBeFocused();
    await draft.evaluate((element) => element.blur());
    await page.keyboard.press("Tab");
    await expect(close).toBeFocused();
    await draft.focus();

    const preview = host.waitForHost("request", "sessionCreation", "previewBranch");
    await draft.fill(BRANCH_INFERENCE_DRAFT);
    const request = await preview;
    expect(request.payload).toMatchObject({
      sourceId: "main",
      attachments: [],
    });
    host.respond(request, {
      branch: "",
      error: "The inference provider failed.",
      needsMoreDetail: false,
    });

    const branch = inbox.getByRole("textbox", { name: "Branch for the new session" });
    await expect(branch).toHaveValue("");
    await expect(inbox.getByRole("alert")).toHaveText(
      "Branch suggestion failed: The inference provider failed. Type a branch to continue.",
    );
    await branch.fill("fix/manual-name");
    await expect(inbox.getByRole("button", { name: "Start" })).toBeEnabled();

    await page.keyboard.press("Escape");
    await expect(modal).toBeHidden();
    await expect(add).toBeFocused();
    await add.click();
    await expect(draft).toHaveValue(BRANCH_INFERENCE_DRAFT);

    await close.focus();
    await page.keyboard.press("Shift+Tab");
    expect(await modal.evaluate((dialog) => dialog.contains(document.activeElement))).toBe(true);
    await close.click();
    await expect(modal).toBeHidden();
    await expect(add).toBeFocused();

    await add.click();
    await page.locator(".session-inbox-surface.open").click({ position: { x: 2, y: 2 } });
    await expect(modal).toBeHidden();
    await expect(add).toBeFocused();
    await add.click();

    await expect(
      openGroup.getByRole("combobox", { name: "Existing branch for the session" }),
    ).toBeVisible();
    await expect(inbox.locator("#session-existing-branches option")).toHaveCount(1);
    await inbox.locator(".session-inbox-row").click();
    await expect(modal).toBeHidden();
    await expect(activeChip).toHaveCount(1);

    branches.push("release/new-since-open");
    await add.click();
    await expect(activeChip).toHaveCount(1);
    await expect(page.locator(".layout-root")).toBeVisible();
    await expect(
      inbox.locator('#session-existing-branches option[value="release/new-since-open"]'),
    ).toHaveCount(1);

    await openGroup
      .getByRole("combobox", { name: "Existing branch for the session" })
      .fill("release/new-since-open");
    const invocation = host.waitForHost("request", "sessions", "invoke");
    await openGroup.getByRole("button", { name: "Open", exact: true }).click();
    const openRequest = await invocation;
    await expect(openGroup.getByRole("button", { name: "Opening branch" })).toBeDisabled();
    await expect(inbox.getByRole("button", { name: "Start", exact: true })).toBeDisabled();
    expect(openRequest.payload).toMatchObject({
      id: CommandIds.newSession,
      args: {
        branch: "release/new-since-open",
        base: "main",
        existing: true,
        prompt: "",
        attachments: [],
        agentProviderId: "claude",
      },
    });
    host.respond(openRequest, {
      ok: true,
      message: null,
      error: null,
      data: { address: session.address },
    });
    await expect(inbox).toBeHidden();
  });

  test("session destination switches to the selected backend's provider catalog", async ({
    page,
  }) => {
    host.onHost("request", "git", "branches", (request) => host.respond(request, ["main"]));
    const remote = await MockHost.start({ distDir });
    remote.onHost("request", "git", "branches", (request) => remote.respond(request, ["main"]));
    try {
      await page.setViewportSize({ width: 1200, height: 800 });
      await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
      await host.waitUntilConnected();
      host.publishHost("remoteAgents", "changed", {
        agents: [{ name: "devbox", url: remote.url, token: "runner-token" }],
      });
      await remote.waitUntilConnected();

      await page.locator(".session-rail-add").click();
      const inbox = page.locator(".session-inbox");
      const provider = inbox.getByRole("combobox", { name: "Agent provider" });
      await provider.selectOption("acp");
      await inbox.getByRole("combobox", { name: "Session location" }).selectOption("remote:devbox");

      await expect(provider).toHaveValue("claude");
    } finally {
      await remote.close();
    }
  });

  test("a remote build mismatch warning can be dismissed", async ({ page }) => {
    const localSession = mockSession("local", "local", "acp");
    const remoteSession = mockSession("remote", "remote", "acp");
    host.setSessions([localSession]);
    const remote = await MockHost.start({
      distDir,
      sessions: [remoteSession],
      buildNumber: "other-build",
    });
    try {
      await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
      await host.waitUntilConnected();
      host.publishHost("remoteAgents", "changed", {
        agents: [{ name: "devbox", url: remote.url, token: "runner-token" }],
      });
      await remote.waitUntilConnected();
      host.publishHost("rail", "changed", {
        lastLocation: "local",
        promoted: ["remote:devbox remote"],
      });
      await page.locator(".session-chip.remote").click();

      const warning = page.locator(".connection-banner-error");
      await expect(warning).toContainText("this client is test");
      const dismiss = warning.getByRole("button", { name: "Dismiss build mismatch warning" });
      await expect(dismiss).toHaveAttribute("title", "Dismiss build mismatch warning");
      await dismiss.click();
      await expect(warning).toHaveCount(0);

      await page.locator(".session-chip:not(.remote)").click();
      await page.locator(".session-chip.remote").click();
      await expect(warning).toHaveCount(0);
    } finally {
      await remote.close();
    }
  });

  test("live fonts update normal DOM and session-owned source typography", async ({ page }) => {
    const session = mockSession("source", "source", "acp");
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
    const main = mockSession("main", "main", "claude");
    const feature = mockSession("feature", "feature", "acp");
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
    const selected = mockSession("selected", "selected", "acp");
    const background = mockSession("background", "background", "acp");
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
    const main = mockSession("main", "main", "claude");
    const feature = mockSession("feature", "feature", "claude");
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
    const oldSession = mockSession("same-slot", "old", "acp");
    host.setSessions([oldSession]);
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();

    const replacement = {
      ...mockSession("same-slot", "new", "acp"),
      address: { slot: "same-slot", incarnation: "replacement-incarnation" },
    };
    const checkpoint = host.checkpoint();
    host.setSessions([replacement]);
    await host.waitForSession(replacement.address, "event", "view", "attach", checkpoint);
    host.publishAgentPane(oldSession.address, {
      providerId: "acp",
      type: "item-completed",
      itemId: "stale",
      itemType: "agentMessage",
      status: "completed",
      text: "stale incarnation transcript",
    });
    host.publishAgentPane(replacement.address, {
      providerId: "acp",
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
    const selected = mockSession("selected", "selected", "acp");
    const background = mockSession("background", "background", "acp");
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
    const local = mockSession("local", "local", "claude");
    const remoteSession = mockSession("remote-acp", "acp", "acp");
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
        promoted: ["remote:devbox remote-acp"],
      });
      remote.publishAgentPane(remoteSession.address, {
        providerId: "acp",
        type: "item-completed",
        itemId: "answer",
        itemType: "agentMessage",
        status: "completed",
        text: "retained remote transcript",
      });
      await expect(page.getByText("retained remote transcript")).toHaveCount(0);

      await page.locator(".session-chip.remote").click();

      await expect(page.getByText("retained remote transcript")).toBeVisible();
      await expect(page.locator(".session-chip.active")).toHaveAttribute("title", /^acp @/);
    } finally {
      await remote.close();
    }
  });

  test("client-owned font zoom stays on the local host with a remote session selected", async ({
    page,
  }) => {
    const local = mockSession("local", "local", "acp");
    const remoteSession = mockSession("remote-acp", "acp", "acp");
    const fontCommand: CommandInfo = {
      id: "weavie.font.increase",
      title: "Increase Font Size",
      runsIn: "core",
      owner: "client",
      executionLane: "weavie.font",
      scope: "session",
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
        promoted: ["remote:devbox remote-acp"],
      });
      await page.locator(".session-chip.remote").click();
      await expect(page.locator(".session-chip.active")).toHaveAttribute("title", /^acp @/);

      const offLocal = host.onHost("request", "commands", "invoke", (message) => {
        host.respond(message, { ok: true, message: null, error: null });
        host.publishHost("settings", "fonts", {
          editor: { family: "monospace", size: 19, weight: "normal" },
          terminal: { family: "monospace", size: 19, weight: "normal" },
        });
      });
      const remoteCheckpoint = remote.checkpoint();
      const request = host.waitForHost("request", "commands", "invoke");

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
      const relayedInvocation = host.waitForHost("request", "commands", "invoke", localCheckpoint);
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

  test("client-owned theme mode updates the page with a remote session selected", async ({
    page,
  }) => {
    const local = mockSession("local", "local", "acp");
    const remoteSession = mockSession("remote-acp", "acp", "acp");
    const themeCommand: CommandInfo = {
      id: "weavie.theme.cycleMode",
      title: "Cycle Theme Mode",
      runsIn: "core",
      owner: "client",
      executionLane: "weavie.theme",
      scope: "session",
      category: "Theme",
      description: "Cycle the appearance mode.",
      aliases: [],
      showInPalette: true,
      keys: ["$mod+Shift+m"],
    };
    const localCatalog = {
      commands: [themeCommand],
      keybindings: [{ key: "$mod+Shift+m", command: themeCommand.id }],
    };
    const theme = (mode: "dark" | "light") => ({
      mode,
      light: { id: "weavie-light" },
      dark: { id: "weavie-dark" },
    });
    const editorBackground = () =>
      page
        .locator("html")
        .evaluate((element) =>
          getComputedStyle(element).getPropertyValue("--weavie-editor-background").trim(),
        );
    const semanticColors = () =>
      page.locator("html").evaluate((element) => {
        const style = getComputedStyle(element);
        return {
          button: style.getPropertyValue("--button-bg").trim(),
          buttonForeground: style.getPropertyValue("--button-fg").trim(),
          buttonHover: style.getPropertyValue("--button-hover-bg").trim(),
          selection: style.getPropertyValue("--list-active-bg").trim(),
          selectionForeground: style.getPropertyValue("--list-active-fg").trim(),
        };
      });

    host.setSessions([local]);
    const remote = await MockHost.start({
      distDir,
      sessions: [remoteSession],
      commandCatalog: {
        commands: [{ ...themeCommand, owner: "backend", title: "Remote duplicate" }],
        keybindings: [{ key: "$mod+Shift+m", command: themeCommand.id }],
      },
    });
    try {
      await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
      await host.waitUntilConnected();
      host.publishHost("commands", "catalog", localCatalog);
      host.publishHost("settings", "theme", theme("dark"));
      await expect(page.locator("html")).toHaveAttribute("data-theme-type", "dark");
      await expect.poll(editorBackground).toBe("#000000");
      await expect.poll(semanticColors).toEqual({
        button: "#54c6a4",
        buttonForeground: "#042019",
        buttonHover: "#63d2b1",
        selection: "#161a21",
        selectionForeground: "#eef2f8",
      });

      host.publishHost("remoteAgents", "changed", {
        agents: [{ name: "devbox", url: remote.url, token: "runner-token" }],
      });
      await remote.waitUntilConnected();
      host.publishHost("rail", "changed", {
        lastLocation: "local",
        promoted: ["remote:devbox remote-acp"],
      });
      await page.locator(".session-chip.remote").click();
      await expect(page.locator(".session-chip.remote.active")).toHaveAttribute("title", /^acp @/);

      const offLocal = host.onHost("request", "commands", "invoke", (message) => {
        host.respond(message, { ok: true, message: "Theme mode: Light.", error: null });
        host.publishHost("settings", "theme", theme("light"));
      });
      const remoteCheckpoint = remote.checkpoint();
      const request = host.waitForHost("request", "commands", "invoke");

      await page.keyboard.press("ControlOrMeta+Shift+m");

      expect((await request).payload).toMatchObject({ id: themeCommand.id });
      await expect(page.locator("html")).toHaveAttribute("data-theme-type", "light");
      await expect.poll(editorBackground).toBe("#e3e2da");
      await expect.poll(semanticColors).toEqual({
        button: "#1f9d78",
        buttonForeground: "#ffffff",
        buttonHover: "#1c8e6c",
        selection: "#dce6df",
        selectionForeground: "#11181f",
      });
      await expect(page.locator(".session-chip.remote.active")).toHaveAttribute("title", /^acp @/);
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
    const session = mockSession("main", "main", "claude");
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

  test("mobile history paging cannot block commands and reconnect catches up", async ({ page }) => {
    const session = mockSession("main", "main", "acp");
    const branches = ["main", "release/responsive-during-history"];
    host.onHost("request", "git", "branches", (request) => host.respond(request, branches));
    host.setSessions([session]);
    const message = (itemId: string, text: string) => ({
      providerId: "acp",
      type: "item-completed",
      itemId,
      itemType: "agentMessage",
      status: "completed",
      text,
    });
    host.setAgentHistory(session.address, {
      generation: 1,
      messages: [message("retained", "retained before reconnect")],
      pageSize: 100,
    });
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    const agentTab = page.getByRole("button", { name: "Agent", exact: true });
    await agentTab.click();
    const surface = page.locator('[data-surface="structured-agent"]');
    await expect(surface).toContainText("retained before reconnect");

    const transcriptBody =
      "Paragraph with **formatted** transcript content and stable paging.\n\n".repeat(315);
    const pagedMessages = Array.from({ length: 571 }, (_, index) =>
      message(`paged-${index}`, `history page ${index}\n\n${transcriptBody}`),
    );
    const historyBytes = Buffer.byteLength(JSON.stringify(pagedMessages));
    expect(historyBytes).toBeGreaterThan(12_000_000);
    expect(historyBytes).toBeLessThan(13_000_000);
    const historyPageSize = 8;
    const expectedHistoryPages = Math.ceil(pagedMessages.length / historyPageSize);
    host.setAgentHistory(session.address, {
      generation: 2,
      messages: pagedMessages,
      pageSize: historyPageSize,
    });
    host.pauseAgentHistoryAfterResponses(1);

    const checkpoint = host.checkpoint();
    host.publishSession(session.address, "agent", "paneReset", {});
    await expect
      .poll(
        () =>
          host.received
            .slice(checkpoint)
            .filter(
              (received) =>
                received.kind === "request" &&
                received.scope === "session" &&
                received.feature === "agent" &&
                received.name === "historyPage",
            ).length,
      )
      .toBe(2);
    const branchCheckpoint = host.checkpoint();
    await page.getByRole("button", { name: "Sessions", exact: true }).click();
    await host.waitForHost("request", "git", "branches", branchCheckpoint);
    await expect(
      page.locator('#session-existing-branches option[value="release/responsive-during-history"]'),
    ).toHaveCount(1);
    host.resumeAgentHistory();
    await agentTab.click();
    await expect
      .poll(
        () =>
          host.received
            .slice(checkpoint)
            .filter(
              (received) =>
                received.kind === "request" &&
                received.scope === "session" &&
                received.feature === "agent" &&
                received.name === "historyPage",
            ).length,
      )
      .toBe(expectedHistoryPages);
    await expect(surface).toContainText("history page 570");
    await expect(surface).not.toContainText("retained before reconnect");

    const completedCheckpoint = host.checkpoint();
    host.pauseHello();
    host.disconnectBridge();
    await host.waitUntilConnected(completedCheckpoint);
    host.setAgentHistory(session.address, {
      generation: 2,
      messages: [...pagedMessages, message("offline", "emitted while offline")],
      pageSize: historyPageSize,
    });
    const catchUpCheckpoint = host.checkpoint();
    host.resumeHello();
    await host.waitForSession(
      session.address,
      "request",
      "agent",
      "historyPage",
      catchUpCheckpoint,
    );
    await expect(surface).toContainText("emitted while offline");
  });

  test("mobile inbox stays usable and truthful through a long catalog and reconnect", async ({
    page,
  }) => {
    const primary = mockSession("main", "main", "acp");
    const branches = ["main"];
    host.onHost("request", "git", "branches", (request) => host.respond(request, branches));
    const dormant = Array.from({ length: 12 }, (_, index) => ({
      ...mockSession(`dormant-${index}`, `feature/dormant-${index}`, "acp"),
      address: null,
      loaded: false,
    }));
    host.setSessions([primary, ...dormant]);
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();

    const inbox = page.locator(".session-inbox");
    await expect(inbox.locator('#session-existing-branches option[value="main"]')).toHaveCount(1);
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
        composerTop: composer.getBoundingClientRect().top,
        inboxClientHeight: element.clientHeight,
        inboxScrollHeight: element.scrollHeight,
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
    expect(geometry.optionTargetRows).toBe(1);
    expect(geometry.inboxScrollHeight).toBeGreaterThan(geometry.inboxClientHeight);
    expect(geometry.listScrollHeight).toBe(geometry.listClientHeight);

    await inbox.evaluate((element) => {
      element.scrollTop = element.scrollHeight;
    });
    const scrolledGeometry = await inbox.evaluate((element) => {
      const composer = element.querySelector<HTMLElement>(".session-composer");
      const lastRow = element.querySelector<HTMLElement>(".session-inbox-row:last-child");
      if (composer === null || lastRow === null) {
        throw new Error("mobile session inbox scroll content is incomplete");
      }
      return {
        composerTop: composer.getBoundingClientRect().top,
        inboxBottom: element.getBoundingClientRect().bottom,
        lastRowBottom: lastRow.getBoundingClientRect().bottom,
        scrollTop: element.scrollTop,
      };
    });
    expect(scrolledGeometry.scrollTop).toBeGreaterThan(0);
    expect(scrolledGeometry.composerTop).toBeLessThan(geometry.composerTop);
    expect(scrolledGeometry.lastRowBottom).toBeLessThanOrEqual(scrolledGeometry.inboxBottom);

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
    branches.push("release/available-after-reconnect");
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
    await expect(
      inbox.locator('#session-existing-branches option[value="release/available-after-reconnect"]'),
    ).toHaveCount(1);
    await expect(
      inbox.getByRole("region", { name: "Open an existing branch" }).getByRole("alert"),
    ).toHaveCount(0);

    const composerTargets = inbox.locator(
      ".session-composer-source select, .session-composer-branch input",
    );
    await expect(composerTargets).not.toHaveCount(0);
    expect(
      Math.min(
        ...(await composerTargets.evaluateAll((targets) =>
          targets.map((target) => target.getBoundingClientRect().height),
        )),
      ),
    ).toBeGreaterThanOrEqual(44);
    await expect(
      inbox.getByRole("combobox", { name: "Existing branch for the session" }),
    ).toBeVisible();

    await page.getByRole("button", { name: "Agent", exact: true }).click();
    const agentComposer = page.locator("[data-agent-composer]");
    await expect(agentComposer).toBeVisible();
    host.publishAgentPane(primary.address, {
      providerId: "acp",
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
        actionsLeft: actions.getBoundingClientRect().left,
        actionsTop: actions.getBoundingClientRect().top,
        buttonHeights: [...actions.querySelectorAll("button")].map(
          (button) => button.getBoundingClientRect().height,
        ),
        buttonFontSize: getComputedStyle(actionButton).fontSize,
        buttonRadius: getComputedStyle(actionButton).borderRadius,
        textareaHeight: textarea.getBoundingClientRect().height,
        textareaRight: textarea.getBoundingClientRect().right,
        textareaTop: textarea.getBoundingClientRect().top,
      };
    });
    expect(
      Math.abs(composerGeometry.actionsTop - composerGeometry.textareaTop),
    ).toBeLessThanOrEqual(1);
    expect(composerGeometry.actionsLeft).toBeGreaterThanOrEqual(composerGeometry.textareaRight);
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
    const session = mockSession("main", "main", "claude");
    host.setSessions([session]);
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    const shellFeature = `terminal.shell.${session.shellTerminals[0]}`;
    await host.waitForSession(session.address, "event", shellFeature, "ready");

    host.publishSession(session.address, shellFeature, "output", {
      dataB64: Buffer.from("AB\x1b[6n").toString("base64"),
      replay: true,
    });
    host.publishSession(session.address, shellFeature, "output", {
      dataB64: Buffer.from("WXYZ\x1b[6n").toString("base64"),
      replay: false,
    });

    const input = await host.waitForSession(session.address, "event", shellFeature, "input");
    const payload = input.payload as { dataB64: string };
    expect(Buffer.from(payload.dataB64, "base64").toString()).toBe("\x1b[1;7R");
    expect(
      host.received.filter(
        (message) =>
          message.scope === "session" &&
          message.feature === shellFeature &&
          message.name === "input",
      ),
    ).toHaveLength(1);
  });

  test("an exact shell target activates its tab without stealing typing focus", async ({
    page,
  }) => {
    const session = mockSession("main", "main", "claude");
    session.shellTerminals = ["shell-a", "shell-b"];
    host.setSessions([session]);
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    const tabs = page.locator(".shell-tab");
    await expect(tabs).toHaveCount(2);
    const input = page.locator("#terminal-activation-focus");
    await page.evaluate(() => {
      const element = document.createElement("input");
      element.id = "terminal-activation-focus";
      document.body.append(element);
    });
    await input.focus();

    host.publishSession(session.address, "view", "focusPane", {
      kind: "terminal:shell",
      terminalId: "shell-b",
    });

    await expect(tabs.nth(1)).toHaveClass(/\bactive\b/);
    await expect(input).toBeFocused();
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
