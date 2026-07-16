import { existsSync, mkdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, type Page, test } from "@playwright/test";
import { MockHost } from "./mock-host";

// Drives the native Codex composer in a real browser against the mock host: it renders the structured agent
// pane from a pushed session-list, feeds it provider-neutral `agent-controls`, and exercises the three new
// features end to end — status line + live picker, `/` slash menu, and Up/Down prompt history — asserting both
// the rendered UI and the `agent-set-control` the picker sends back. Screenshots land in .recordings for review.

const here = dirname(fileURLToPath(import.meta.url));
const distDir = join(here, "..", "dist");
const shotsDir = join(here, ".recordings", "codex-composer");

const codexChip = {
  id: "cx",
  label: "codex",
  active: true,
  loaded: true,
  primary: true,
  providerId: "codex",
  agentSurface: "structured",
  agentInputProtocol: 2,
  status: "idle",
  hue: 150,
  monogram: "C",
};

const controls = {
  type: "agent-controls",
  slot: "cx",
  workspace: "/repo",
  state: {
    modelControl: {
      value: "gpt-5.5",
      valueLabel: "GPT-5.5 (Medium)",
      models: [
        {
          id: "gpt-5.5",
          label: "GPT-5.5",
          current: true,
          effort: "medium",
          efforts: [
            { id: "low", label: "Low", description: "Fast responses." },
            { id: "medium", label: "Medium", description: "Balanced." },
            { id: "high", label: "High", description: "Deeper reasoning." },
          ],
          fastTier: "priority",
          fastOn: false,
        },
        {
          id: "gpt-5.4-mini",
          label: "GPT-5.4 mini",
          current: false,
          effort: "low",
          efforts: [{ id: "low", label: "Low", description: "Fast responses." }],
          fastTier: "",
          fastOn: false,
        },
      ],
    },
    axes: [
      {
        id: "collaborationMode",
        label: "Mode",
        value: "default",
        valueLabel: "Default",
        options: [
          { id: "plan", label: "Plan", description: null },
          { id: "default", label: "Default", description: null },
        ],
        commandId: "weavie.agent.togglePlanMode",
      },
      {
        id: "approvalPolicy",
        label: "Approvals",
        value: "on-request",
        valueLabel: "On request",
        options: [
          { id: "on-request", label: "On request", description: null },
          { id: "never", label: "Never", description: null },
        ],
        commandId: "weavie.agent.selectApprovalPolicy",
      },
      {
        id: "sandbox",
        label: "Sandbox",
        value: "workspace-write",
        valueLabel: "Workspace write",
        options: [
          { id: "read-only", label: "Read only", description: null },
          { id: "workspace-write", label: "Workspace write", description: null },
        ],
        commandId: "weavie.agent.selectSandbox",
      },
    ],
    slash: [
      {
        id: "builtin:model",
        name: "model",
        description: "Switch the model, effort, or Fast Mode",
        commandId: "weavie.agent.selectModel",
        insertText: null,
        skillName: null,
      },
      {
        id: "builtin:plan",
        name: "plan",
        description: "Toggle Plan mode",
        commandId: "weavie.agent.togglePlanMode",
        insertText: null,
        skillName: null,
      },
      {
        id: "skill:review-pr",
        name: "review-pr",
        description: "Review a pull request.",
        commandId: null,
        insertText: null,
        skillName: "review-pr",
      },
    ],
  },
};

// The controls with the active model's Fast Mode on, as the host echoes it back after a toggle.
const fastOnControls = {
  ...controls,
  state: {
    ...controls.state,
    modelControl: {
      ...controls.state.modelControl,
      valueLabel: "GPT-5.5 (Medium) ⚡",
      models: controls.state.modelControl.models.map((model) =>
        model.current ? { ...model, fastOn: true } : model,
      ),
    },
  },
};

const planControls = {
  ...controls,
  state: {
    ...controls.state,
    axes: controls.state.axes.map((axis) =>
      axis.id === "collaborationMode" ? { ...axis, value: "plan", valueLabel: "Plan" } : axis,
    ),
  },
};

const paneMessage = (message: Record<string, unknown>) => ({
  type: "agent-pane",
  slot: "cx",
  workspace: "/repo",
  message: { providerId: "codex", ...message },
});

const userMessage = (text: string) => paneMessage({ type: "user-message", text });

// The agent slice of the command catalog, as the host pushes it — the UI reads all key labels from here.
const agentCommand = (id: string, title: string, when: string, keys: string[]) => ({
  id,
  title,
  runsIn: "web",
  description: "",
  aliases: [],
  showInPalette: true,
  when,
  keys,
});

const approvalWhen = "agentFocused && agentApprovalPending";
const catalog = {
  type: "commands",
  commands: [
    agentCommand("weavie.agent.submit", "Submit Agent Prompt", "agentComposerFocused", ["enter"]),
    agentCommand("weavie.agent.interrupt", "Interrupt Agent Turn", "agentFocused", ["escape"]),
    agentCommand("weavie.agent.togglePlanMode", "Toggle Agent Plan Mode", "agentFocused", [
      "shift+tab",
    ]),
    agentCommand("weavie.agent.approve", "Approve Agent Request", approvalWhen, ["alt+y"]),
    agentCommand("weavie.agent.approveForSession", "Approve For Session", approvalWhen, [
      "alt+shift+y",
    ]),
    agentCommand("weavie.agent.decline", "Decline Agent Request", approvalWhen, ["alt+n"]),
    agentCommand("weavie.pr.openCurrent", "Open Current Pull Request", "pullRequestAvailable", [
      "$mod+shift+g",
    ]),
    agentCommand("weavie.review.open", "Review Changes", "", []),
  ],
  keybindings: [
    { key: "enter", command: "weavie.agent.submit", when: "agentComposerFocused" },
    { key: "escape", command: "weavie.agent.interrupt", when: "agentFocused" },
    { key: "shift+tab", command: "weavie.agent.togglePlanMode", when: "agentFocused" },
    { key: "alt+y", command: "weavie.agent.approve", when: approvalWhen },
    { key: "alt+shift+y", command: "weavie.agent.approveForSession", when: approvalWhen },
    { key: "alt+n", command: "weavie.agent.decline", when: approvalWhen },
    {
      key: "$mod+shift+g",
      command: "weavie.pr.openCurrent",
      when: "pullRequestAvailable",
    },
  ],
};

test.beforeAll(() => {
  if (!existsSync(join(distDir, "index.html"))) {
    throw new Error(`built app not found at ${distDir}; run \`pnpm run build\` first`);
  }
  mkdirSync(shotsDir, { recursive: true });
});

test.describe("Codex composer", () => {
  let host: MockHost;

  test.beforeEach(async () => {
    host = await MockHost.start({ distDir });
  });

  test.afterEach(async () => {
    await host.close();
  });

  // Mounts the Codex session and its control surface after `ready` proves App's listeners are installed.
  async function mountCodex(page: Page): Promise<void> {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitForMessage("ready");
    const statusLine = page.locator(".agent-status-line");
    host.pushToWeb({ type: "session-list", sessions: [codexChip] });
    host.pushToWeb(controls);
    await expect(statusLine).toBeVisible();
  }

  test("status line shows model, mode, approvals, and sandbox", async ({ page }) => {
    await mountCodex(page);

    // Model / effort / Fast collapse into one segment; the generic control axes follow it.
    const segments = page.locator(".agent-status-segment");
    await expect(segments).toHaveCount(4);
    await expect(page.locator(".agent-status-model")).toContainText("GPT-5.5 (Medium)");
    await expect(page.locator(".agent-status-toggle")).toHaveCount(0);
    await expect(segments.nth(1)).toContainText("Default");
    await expect(segments.nth(2)).toContainText("On request");
    await expect(segments.nth(3)).toContainText("Workspace write");
    await page.screenshot({ path: join(shotsDir, "01-status-line.png") });
    await page.locator(".agent-compose").screenshot({ path: join(shotsDir, "00-compose-row.png") });
  });

  test("status line shows the complete Review diff and opens it", async ({ page }) => {
    await mountCodex(page);
    host.pushToWeb(catalog);
    host.pushToWeb({
      type: "turn-changes",
      label: "",
      files: [
        { path: "/workspace/one.ts", name: "one.ts", added: 7, removed: 1, line: 2 },
        { path: "/workspace/two.ts", name: "two.ts", added: 5, removed: 3, line: 4 },
      ],
    });

    const counts = page.locator(".agent-status-review");
    await expect(counts).toHaveText("+12/-4");
    await expect(counts).toHaveAttribute(
      "title",
      "Open review — 12 lines added, 4 removed across 2 files",
    );
    await counts.click();
    await expect
      .poll(() => host.received.some((message) => message.type === "get-turn-diff"))
      .toBe(true);

    host.pushToWeb({ type: "turn-changes", label: "", files: [] });
    await expect(counts).toHaveCount(0);
  });

  test("Shift+Tab and /plan share the Plan-mode command and advertise its binding", async ({
    page,
  }) => {
    await mountCodex(page);
    host.pushToWeb(catalog);

    const mode = page.locator(".agent-status-segment", { hasText: "Default" });
    await expect(mode).toHaveAttribute("title", /Shift\+Tab/);
    const textarea = page.locator("[data-agent-composer] textarea");
    await textarea.click();
    await page.keyboard.press("Shift+Tab");
    expect(await host.waitForMessage("agent-set-control")).toMatchObject({
      slot: "cx",
      axis: "collaborationMode",
      value: "plan",
    });

    host.pushToWeb(planControls);
    await expect(page.locator(".agent-status-segment", { hasText: "Plan" })).toBeVisible();
    await textarea.fill("/plan");
    const row = page.locator(".agent-slash-option", { hasText: "/plan" });
    await expect(row).toContainText("Shift+Tab");
    await page.screenshot({ path: join(shotsDir, "13-plan-mode.png") });
    await page.keyboard.press("Enter");
    await expect
      .poll(
        () => host.received.filter((message) => message.type === "agent-set-control").at(-1)?.value,
      )
      .toBe("default");
  });

  test("status line links the current branch's pull request", async ({ page }) => {
    await mountCodex(page);
    const url = `${host.url}/pull/123`;
    host.pushToWeb(catalog);
    host.pushToWeb({ type: "git-status", branch: "feat/native-ui-pr", dirty: false });
    host.pushToWeb({
      type: "pull-request-status",
      slot: "cx",
      branch: "feat/native-ui-pr",
      pullRequest: { number: 123, url },
      error: null,
    });

    const link = page.locator(".agent-status-pr");
    await expect(link).toHaveText("#123");
    const modifier = process.platform === "darwin" ? "⌘" : "Ctrl";
    await expect(link).toHaveAttribute("title", new RegExp(`${modifier}\\+Shift\\+G`));
    const popupPromise = page.waitForEvent("popup");
    await link.click();
    await expect(await popupPromise).toHaveURL(url);

    host.pushToWeb({ type: "git-status", branch: "another-branch", dirty: false });
    await expect(link).toHaveCount(0);
  });

  test("subagent completion keeps the primary turn working until its own result", async ({
    page,
  }) => {
    await mountCodex(page);
    host.pushToWeb(
      paneMessage({
        type: "user-message",
        threadId: "thread-primary",
        text: "Do the work",
      }),
    );
    host.pushToWeb(
      paneMessage({
        type: "turn-started",
        threadId: "thread-primary",
        turnId: "turn-primary",
        isPrimaryThread: true,
      }),
    );
    host.pushToWeb(
      paneMessage({
        type: "turn-started",
        threadId: "thread-subagent",
        turnId: "turn-subagent",
        isPrimaryThread: false,
      }),
    );
    host.pushToWeb(
      paneMessage({
        type: "item-completed",
        threadId: "thread-subagent",
        turnId: "turn-subagent",
        itemId: "subagent-update",
        itemType: "agentMessage",
        isPrimaryThread: false,
        text: "Subagent found the cause.",
      }),
    );
    host.pushToWeb(
      paneMessage({
        type: "turn-completed",
        threadId: "thread-subagent",
        turnId: "turn-subagent",
        isPrimaryThread: false,
      }),
    );

    await expect(page.locator(".agent-working")).toBeVisible();
    await expect(page.locator(".agent-compose button[type='submit']")).toHaveText("Steer");
    await expect(
      page.locator(".agent-markdown", { hasText: "Subagent found the cause." }),
    ).toBeVisible();

    host.pushToWeb(
      paneMessage({
        type: "item-completed",
        threadId: "thread-primary",
        turnId: "turn-primary",
        itemId: "primary-result",
        itemType: "agentMessage",
        isPrimaryThread: true,
        text: "Primary work is done.",
      }),
    );
    host.pushToWeb(
      paneMessage({
        type: "turn-completed",
        threadId: "thread-primary",
        turnId: "turn-primary",
        isPrimaryThread: true,
      }),
    );

    await expect(page.locator(".agent-working")).toHaveCount(0);
    await expect(page.locator(".agent-compose button[type='submit']")).toHaveText("Run");
    await expect(
      page.locator(".agent-entry-result", { hasText: "Primary work is done." }),
    ).toContainText("Results");
  });

  test("the model picker switches model via the models column", async ({ page }) => {
    await mountCodex(page);

    await page.locator(".agent-status-model").click();
    const picker = page.locator(".agent-model-picker");
    await expect(picker).toBeVisible();
    await expect(picker.locator(".agent-model-row")).toHaveCount(2);
    // The focused model's submenu shows on the right: GPT-5.5's three efforts plus Fast.
    await expect(picker.locator(".agent-model-picker-sub .agent-model-sub-item")).toHaveCount(4);
    await page.screenshot({ path: join(shotsDir, "02-model-picker.png") });

    // Current model (gpt-5.5) is focused; Down moves to gpt-5.4-mini, Enter selects it.
    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("Enter");

    const set = await host.waitForMessage("agent-set-control");
    expect(set).toMatchObject({ slot: "cx", axis: "model", value: "gpt-5.4-mini" });
    await expect(picker).toBeHidden();
  });

  test("picking an effort in the model submenu applies it to the current model", async ({
    page,
  }) => {
    await mountCodex(page);

    await page.locator(".agent-status-model").click();
    // ArrowRight enters the current model's submenu, focused on its current effort (medium).
    await page.keyboard.press("ArrowRight");
    const sub = page.locator(".agent-model-picker-sub .agent-model-sub-item");
    await expect(sub).toHaveCount(4); // low / medium / high + Fast
    await page.screenshot({ path: join(shotsDir, "02b-effort-submenu.png") });

    // Down moves medium → high, Enter applies it. gpt-5.5 is current, so only the effort is sent.
    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("Enter");

    const set = await host.waitForMessage("agent-set-control");
    expect(set).toMatchObject({ slot: "cx", axis: "effort", value: "high" });
    await expect(page.locator(".agent-model-picker")).toBeHidden();
  });

  test("toggling Fast in the submenu switches the tier and shows the bolt", async ({ page }) => {
    await mountCodex(page);

    await page.locator(".agent-status-model").click();
    const fastItem = page.locator(".agent-model-fast-item");
    await expect(fastItem).toBeVisible();
    await expect(fastItem).not.toHaveClass(/on/);
    await fastItem.click();

    const set = await host.waitForMessage("agent-set-control");
    expect(set).toMatchObject({ slot: "cx", axis: "serviceTier", value: "priority" });

    // The host echoes Fast on; the submenu item reads on and the status-line label gains the bolt.
    host.pushToWeb(fastOnControls);
    await expect(fastItem).toHaveClass(/on/);
    await expect(page.locator(".agent-status-model")).toContainText("⚡");
    await page.screenshot({ path: join(shotsDir, "12-fast-on.png") });
  });

  test("keyboard focus in the submenu survives a host re-push", async ({ page }) => {
    await mountCodex(page);

    await page.locator(".agent-status-model").click();
    // Into GPT-5.5's submenu, then down to the Fast row (efforts low/medium/high + Fast).
    await page.keyboard.press("ArrowRight");
    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("ArrowDown");
    const fastItem = page.locator(".agent-model-fast-item");
    await expect(fastItem).toHaveClass(/active/);

    // A control re-push (which every SetControl triggers) must not snap focus back out of the submenu.
    host.pushToWeb(controls);
    await expect(fastItem).toHaveClass(/active/);
  });

  test("the approvals picker keeps its keyboard highlight across a host re-push", async ({
    page,
  }) => {
    await mountCodex(page);

    await page.locator(".agent-status-segment", { hasText: "On request" }).click();
    const options = page.locator(".agent-control-picker .agent-control-option");
    await expect(options.nth(0)).toHaveClass(/active/); // seeded on the current value
    await page.keyboard.press("ArrowDown");
    await expect(options.nth(1)).toHaveClass(/active/);

    // A control re-push (which every SetControl triggers) must not re-seed the highlight mid-use.
    host.pushToWeb(controls);
    await expect(options.nth(1)).toHaveClass(/active/);

    // Keyboard selection still applies the highlighted option after the re-push.
    await page.keyboard.press("Enter");
    const set = await host.waitForMessage("agent-set-control");
    expect(set).toMatchObject({ slot: "cx", axis: "approvalPolicy", value: "never" });
  });

  test("typing / opens the slash menu and a skill stages a chip", async ({ page }) => {
    await mountCodex(page);

    const textarea = page.locator("[data-agent-composer] textarea");
    await textarea.click();
    await page.keyboard.type("/");

    const menu = page.locator(".agent-slash-menu");
    await expect(menu).toBeVisible();
    await expect(menu.locator(".agent-slash-option")).toHaveCount(3);
    await expect(menu).toContainText("/model");
    await expect(menu).toContainText("/plan");
    await expect(menu).toContainText("/review-pr");
    await page.screenshot({ path: join(shotsDir, "03-slash-menu.png") });

    // Narrow to the skill and accept it; it stages as a chip (structured skill input) and clears the query.
    await page.keyboard.type("rev");
    await expect(menu.locator(".agent-slash-option")).toHaveCount(1);
    await page.keyboard.press("Enter");
    await expect(menu).toBeHidden();
    await expect(textarea).toHaveValue("");
    const chip = page.locator(".agent-skill-chip", { hasText: "/review-pr" });
    await expect(chip).toBeVisible();
    await page.screenshot({ path: join(shotsDir, "05-skill-chip.png") });

    // Removing the chip un-stages the skill.
    await chip.locator("button").click();
    await expect(page.locator(".agent-skill-chip")).toHaveCount(0);
  });

  // Pins the composer's turn-progress wiring: the working row (with elapsed time), the Run→Steer submit
  // relabel, the turn-only Interrupt button, and the amber waiting state while an approval is pending.
  test("the working row tracks the turn: working, waiting, back to working, gone", async ({
    page,
  }) => {
    await mountCodex(page);
    const working = page.locator(".agent-working");
    const submit = page.locator("[data-agent-composer] button[type=submit]");
    const interrupt = page.locator("[data-agent-composer] button", { hasText: "Interrupt" });

    // Idle: no row, no Interrupt button, submit reads Run.
    await expect(working).toHaveCount(0);
    await expect(interrupt).toHaveCount(0);
    await expect(submit).toHaveText("Run");

    host.pushToWeb(paneMessage({ type: "turn-started", turnId: "t1", status: "inProgress" }));
    await expect(working).toBeVisible();
    await expect(working.locator(".agent-working-label")).toHaveText("Working");
    await expect(working.locator(".agent-working-time")).toHaveText(/^\d+s$/);
    await expect(submit).toHaveText("Steer");
    await expect(interrupt).toBeVisible();
    await page.screenshot({ path: join(shotsDir, "06-working-row.png") });

    host.pushToWeb(
      paneMessage({
        type: "approval-requested",
        itemId: "a1",
        status: "pending",
        summary: "Run: dotnet test",
      }),
    );
    await expect(working).toHaveClass(/waiting/);
    await expect(working.locator(".agent-working-label")).toHaveText("Waiting on your approval");
    await page.screenshot({ path: join(shotsDir, "07-waiting-row.png") });

    host.pushToWeb(paneMessage({ type: "approval-resolved", itemId: "a1", status: "accept" }));
    await expect(working).not.toHaveClass(/waiting/);
    await expect(working.locator(".agent-working-label")).toHaveText("Working");

    host.pushToWeb(paneMessage({ type: "turn-completed", turnId: "t1", status: "interrupted" }));
    await expect(working).toHaveCount(0);
    await expect(submit).toHaveText("Run");
    await expect(interrupt).toHaveCount(0);
  });

  // The regression this branch fixes: the elapsed clock is anchored to the turn's arrival (stamped in the
  // message stream), not to when the composer mounted — so leaving a mid-turn session and coming back keeps
  // it counting real wall-clock instead of restarting near zero.
  test("the working timer keeps counting across a session switch — it never resets", async ({
    page,
  }) => {
    await mountCodex(page);
    const secondChip = { ...codexChip, id: "cx2", monogram: "D", primary: false };
    const sessionList = (activeId: string) => ({
      type: "session-list",
      sessions: [
        { ...codexChip, active: activeId === "cx" },
        { ...secondChip, active: activeId === "cx2" },
      ],
    });
    const working = page.locator(".agent-working");
    const timeText = working.locator(".agent-working-time");
    const readSeconds = async (): Promise<number> => {
      const text = (await timeText.textContent()) ?? "";
      const match = text.match(/(?:(\d+)m\s*)?(\d+)s/);
      return match === null ? -1 : (match[1] ? Number(match[1]) * 60 : 0) + Number(match[2]);
    };

    // Start a turn on the Codex session; let its timer tick past a couple of seconds so a reset would be stark.
    host.pushToWeb(paneMessage({ type: "turn-started", turnId: "t1", status: "inProgress" }));
    await expect(working).toBeVisible();
    await expect.poll(readSeconds, { timeout: 8_000 }).toBeGreaterThanOrEqual(2);
    const before = await readSeconds();

    // Switch to a different session (no active turn) — the Codex working row leaves with it.
    host.pushToWeb(sessionList("cx2"));
    host.pushToWeb({ ...controls, slot: "cx2" });
    await expect(working).toHaveCount(0);

    // Sit on the other session for several wall-clock seconds, then return to the still-running Codex turn.
    await page.waitForTimeout(4_000);
    host.pushToWeb(sessionList("cx"));
    await expect(working).toBeVisible();

    // The clock reflects total time since the turn began: not less than before (never reset) and grown by
    // roughly the seconds spent away.
    const after = await readSeconds();
    expect(after).toBeGreaterThanOrEqual(before);
    expect(after).toBeGreaterThanOrEqual(before + 2);
    await page.screenshot({ path: join(shotsDir, "11-timer-after-switch.png") });
  });

  // Pins the idle welcome: provider name, catalog-driven key hints, and the teaching placeholder.
  test("the idle pane teaches the keyboard paths", async ({ page }) => {
    await mountCodex(page);
    host.pushToWeb(catalog);

    const empty = page.locator(".agent-empty");
    await expect(empty).toBeVisible();
    await expect(empty.locator(".agent-empty-title")).toHaveText("Codex");
    await expect(empty.locator("kbd")).toHaveText(["Enter", "/", "↑", "Escape"]);
    await expect(page.locator("[data-agent-composer] textarea")).toHaveAttribute(
      "placeholder",
      "Write a prompt — / for commands and skills",
    );
    await page.screenshot({ path: join(shotsDir, "08-empty-state.png") });
  });

  // Pins the informed-approval flow: the card shows the command under review, the buttons wear their
  // chords, and Alt+Y answers the pending request from the keyboard.
  test("an approval card shows the command and answers to Alt+Y", async ({ page }) => {
    await mountCodex(page);
    host.pushToWeb(catalog);
    host.pushToWeb(paneMessage({ type: "turn-started", turnId: "t1", status: "inProgress" }));
    host.pushToWeb(
      paneMessage({
        type: "approval-requested",
        itemId: "a1",
        status: "pending",
        summary: "Wants to run the test suite.",
        text: "dotnet test tests/Weavie.Hosting.Tests",
      }),
    );

    const card = page.locator(".agent-entry-request");
    await expect(card).toContainText("dotnet test tests/Weavie.Hosting.Tests");
    const accept = card.locator("button", { hasText: "Accept" }).first();
    await expect(accept.locator(".agent-key-chip")).toHaveText("Alt+Y");
    await page.screenshot({ path: join(shotsDir, "09-approval-card.png") });

    await page.locator("[data-agent-composer] textarea").click();
    await page.keyboard.press("Alt+y");
    const decision = await host.waitForMessage("agent-approval");
    expect(decision).toMatchObject({ slot: "cx", requestId: "a1", decision: "accept" });
  });

  // Regression: once the approval resolves, its decision buttons must go — the card is no longer actionable.
  // The header status flips reactively; the buttons must flip with it in the same live update (no re-mount).
  test("a resolved approval drops its decision buttons in place", async ({ page }) => {
    await mountCodex(page);
    host.pushToWeb(catalog);
    host.pushToWeb(paneMessage({ type: "turn-started", turnId: "t1", status: "inProgress" }));
    host.pushToWeb(
      paneMessage({
        type: "approval-requested",
        itemId: "a1",
        status: "pending",
        summary: "Wants to run the test suite.",
        text: "dotnet test tests/Weavie.Hosting.Tests",
      }),
    );

    const card = page.locator(".agent-entry-request");
    const buttons = card.locator(".agent-approval-actions button");
    await expect(buttons.filter({ hasText: "Accept" }).first()).toBeVisible();

    host.pushToWeb(
      paneMessage({ type: "approval-resolved", itemId: "a1", status: "acceptForSession" }),
    );

    await expect(card.locator(".agent-entry-status")).toHaveText("accepted for session");
    await expect(buttons).toHaveCount(0);
  });

  // Regression: a turn boundary must not strip a still-unresolved approval of its hotkeys. The chip and the
  // chord derive from resolution state, not turn state, so the card stays keyboard-answerable while it shows
  // its buttons — even after a turn-completed races in ahead of the answer.
  test("an unresolved approval keeps its hotkeys after the turn reports completed", async ({
    page,
  }) => {
    await mountCodex(page);
    host.pushToWeb(catalog);
    host.pushToWeb(paneMessage({ type: "turn-started", turnId: "t1", status: "inProgress" }));
    host.pushToWeb(
      paneMessage({
        type: "approval-requested",
        itemId: "a1",
        status: "pending",
        summary: "Wants to run the test suite.",
        text: "dotnet test tests/Weavie.Hosting.Tests",
      }),
    );
    host.pushToWeb(paneMessage({ type: "turn-completed", turnId: "t1", status: "completed" }));

    const card = page.locator(".agent-entry-request");
    const accept = card.locator("button", { hasText: "Accept" }).first();
    await expect(accept.locator(".agent-key-chip")).toHaveText("Alt+Y");

    await page.locator("[data-agent-composer] textarea").click();
    await page.keyboard.press("Alt+y");
    const decision = await host.waitForMessage("agent-approval");
    expect(decision).toMatchObject({ slot: "cx", requestId: "a1", decision: "accept" });
  });

  // Pins the follow threshold and pill: staying within three lines keeps following; scrolling farther up pauses it.
  test("scrolling beyond three lines shows the jump-to-latest pill", async ({ page }) => {
    await mountCodex(page);
    for (let i = 0; i < 40; i += 1) {
      host.pushToWeb(userMessage(`prompt ${i}\nwith\nseveral\nlines`));
    }

    const body = page.locator(".agent-body");
    const pill = page.locator(".agent-follow-pill");
    await expect(page.locator(".agent-entry").first()).toBeVisible();
    await expect(pill).toHaveCount(0);

    const scrollLinesFromBottom = (lines: number): Promise<void> =>
      body.evaluate(
        (el, linesFromBottom) =>
          new Promise<void>((resolve) => {
            el.addEventListener("scroll", () => resolve(), { once: true });
            const lineHeight = Number.parseFloat(getComputedStyle(el).lineHeight);
            el.scrollTo({
              top: el.scrollHeight - el.clientHeight - lineHeight * linesFromBottom,
            });
          }),
        lines,
      );

    await scrollLinesFromBottom(3);
    await expect(pill).toHaveCount(0);
    host.pushToWeb(userMessage("near-bottom follow check"));
    await expect
      .poll(() => body.evaluate((el) => el.scrollHeight - el.scrollTop - el.clientHeight))
      .toBeLessThan(1);

    await scrollLinesFromBottom(4);
    await expect(pill).toBeVisible();
    await page.screenshot({ path: join(shotsDir, "10-follow-pill.png") });

    await pill.click();
    await expect(pill).toHaveCount(0);
    await expect
      .poll(() => body.evaluate((el) => el.scrollHeight - el.scrollTop - el.clientHeight))
      .toBeLessThan(1);
    host.pushToWeb(userMessage("follow after jump to latest"));
    await expect(page.getByText("follow after jump to latest", { exact: true })).toBeVisible();
    await expect
      .poll(() => body.evaluate((el) => el.scrollHeight - el.scrollTop - el.clientHeight))
      .toBeLessThan(1);
  });

  test("Up/Down recall previously submitted prompts", async ({ page }) => {
    await mountCodex(page);
    host.pushToWeb(userMessage("first prompt"));
    host.pushToWeb(userMessage("second prompt"));

    const textarea = page.locator("[data-agent-composer] textarea");
    await textarea.click();
    await page.keyboard.type("a fresh draft");

    await page.keyboard.press("ArrowUp");
    await expect(textarea).toHaveValue("second prompt");
    await page.keyboard.press("ArrowUp");
    await expect(textarea).toHaveValue("first prompt");
    await page.screenshot({ path: join(shotsDir, "04-history-recall.png") });
    await page.keyboard.press("ArrowDown");
    await expect(textarea).toHaveValue("second prompt");
    await page.keyboard.press("ArrowDown");
    await expect(textarea).toHaveValue("a fresh draft");
  });
});
