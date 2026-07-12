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
    axes: [
      {
        id: "model",
        label: "Model",
        value: "gpt-5.5",
        valueLabel: "GPT-5.5",
        toggle: false,
        options: [
          { id: "gpt-5.5", label: "GPT-5.5", description: "Frontier model." },
          { id: "gpt-5.4-mini", label: "GPT-5.4 mini", description: "Fast model." },
        ],
      },
      {
        id: "effort",
        label: "Effort",
        value: "medium",
        valueLabel: "Medium",
        toggle: false,
        options: [
          { id: "low", label: "Low", description: "Fast responses." },
          { id: "medium", label: "Medium", description: "Balanced." },
          { id: "high", label: "High", description: "Deeper reasoning." },
        ],
      },
      {
        id: "serviceTier",
        label: "Speed",
        value: "standard",
        valueLabel: "Standard",
        toggle: true,
        options: [
          { id: "standard", label: "Standard", description: "Normal speed and usage." },
          { id: "priority", label: "Fast", description: "1.5x speed, increased usage" },
        ],
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
      },
    ],
    slash: [
      {
        id: "builtin:model",
        name: "model",
        description: "Switch the model for this session",
        commandId: "weavie.agent.selectModel",
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
    agentCommand("weavie.agent.approve", "Approve Agent Request", approvalWhen, ["alt+y"]),
    agentCommand("weavie.agent.approveForSession", "Approve For Session", approvalWhen, [
      "alt+shift+y",
    ]),
    agentCommand("weavie.agent.decline", "Decline Agent Request", approvalWhen, ["alt+n"]),
  ],
  keybindings: [
    { key: "enter", command: "weavie.agent.submit", when: "agentComposerFocused" },
    { key: "escape", command: "weavie.agent.interrupt", when: "agentFocused" },
    { key: "alt+y", command: "weavie.agent.approve", when: approvalWhen },
    { key: "alt+shift+y", command: "weavie.agent.approveForSession", when: approvalWhen },
    { key: "alt+n", command: "weavie.agent.decline", when: approvalWhen },
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

  // Mounts the Codex session and its control surface, retrying the pushes until the status line renders (App
  // registers its host listener only after mount, so an early push can land before anyone is listening).
  async function mountCodex(page: Page): Promise<void> {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitForMessage("ready");
    const statusLine = page.locator(".agent-status-line");
    await expect(async () => {
      host.pushToWeb({ type: "session-list", sessions: [codexChip] });
      host.pushToWeb(controls);
      await expect(statusLine).toBeVisible({ timeout: 1000 });
    }).toPass({ timeout: 20_000 });
  }

  test("status line shows model, effort, Fast toggle, approvals, and sandbox", async ({ page }) => {
    await mountCodex(page);

    // The Fast toggle renders as its own chip, so the picker segments are model, effort, approvals, sandbox.
    const segments = page.locator(".agent-status-segment");
    await expect(segments).toHaveCount(4);
    await expect(segments.nth(0)).toContainText("Model");
    await expect(segments.nth(0)).toContainText("GPT-5.5");
    await expect(segments.nth(1)).toContainText("Effort");
    await expect(segments.nth(1)).toContainText("Medium");
    await expect(segments.nth(2)).toContainText("On request");
    await expect(segments.nth(3)).toContainText("Workspace write");

    const fast = page.locator(".agent-status-toggle");
    await expect(fast).toHaveText("Fast");
    await expect(fast).not.toHaveClass(/on/); // off by default
    await page.screenshot({ path: join(shotsDir, "01-status-line.png") });
    await page.locator(".agent-compose").screenshot({ path: join(shotsDir, "00-compose-row.png") });
  });

  test("the Fast toggle switches the service tier live and highlights when on", async ({
    page,
  }) => {
    await mountCodex(page);

    const fast = page.locator(".agent-status-toggle");
    await expect(fast).not.toHaveClass(/on/);
    await fast.click();

    const set = await host.waitForMessage("agent-set-control");
    expect(set).toMatchObject({ slot: "cx", axis: "serviceTier", value: "priority" });

    // The host echoes the new state back; the chip then reads as on.
    host.pushToWeb({
      ...controls,
      state: {
        ...controls.state,
        axes: controls.state.axes.map((axis) =>
          axis.id === "serviceTier" ? { ...axis, value: "priority", valueLabel: "Fast" } : axis,
        ),
      },
    });
    await expect(fast).toHaveClass(/on/);
    await page.screenshot({ path: join(shotsDir, "12-fast-on.png") });
  });

  test("the model picker applies a live change back to the host", async ({ page }) => {
    await mountCodex(page);

    await page.locator(".agent-status-segment", { hasText: "Model" }).click();
    const picker = page.locator(".agent-control-picker");
    await expect(picker).toBeVisible();
    await expect(picker.locator(".agent-control-option")).toHaveCount(2);
    await page.screenshot({ path: join(shotsDir, "02-model-picker.png") });

    // Current value (gpt-5.5) is highlighted; Down moves to gpt-5.4-mini, Enter applies it.
    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("Enter");

    const set = await host.waitForMessage("agent-set-control");
    expect(set).toMatchObject({ slot: "cx", axis: "model", value: "gpt-5.4-mini" });
    await expect(picker).toBeHidden();
  });

  test("the effort picker applies a live change back to the host", async ({ page }) => {
    await mountCodex(page);

    await page.locator(".agent-status-segment", { hasText: "Effort" }).click();
    const picker = page.locator(".agent-control-picker");
    await expect(picker).toBeVisible();
    await expect(picker.locator(".agent-control-option")).toHaveCount(3);

    // Current value (medium) is highlighted; Down moves to high, Enter applies it.
    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("Enter");

    const set = await host.waitForMessage("agent-set-control");
    expect(set).toMatchObject({ slot: "cx", axis: "effort", value: "high" });
    await expect(picker).toBeHidden();
  });

  test("typing / opens the slash menu and a skill stages a chip", async ({ page }) => {
    await mountCodex(page);

    const textarea = page.locator("[data-agent-composer] textarea");
    await textarea.click();
    await page.keyboard.type("/");

    const menu = page.locator(".agent-slash-menu");
    await expect(menu).toBeVisible();
    await expect(menu.locator(".agent-slash-option")).toHaveCount(2);
    await expect(menu).toContainText("/model");
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

    host.pushToWeb(paneMessage({ type: "turn-interrupted", turnId: "t1", status: "interrupted" }));
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

  // Pins the follow pill: scrolling up pauses follow and shows the pill; clicking it re-sticks.
  test("scrolling up shows the jump-to-latest pill", async ({ page }) => {
    await mountCodex(page);
    for (let i = 0; i < 40; i += 1) {
      host.pushToWeb(userMessage(`prompt ${i}\nwith\nseveral\nlines`));
    }

    const body = page.locator(".agent-body");
    const pill = page.locator(".agent-follow-pill");
    await expect(page.locator(".agent-entry").first()).toBeVisible();
    await expect(pill).toHaveCount(0);

    await body.evaluate((el) => el.scrollTo({ top: 0 }));
    await expect(pill).toBeVisible();
    await page.screenshot({ path: join(shotsDir, "10-follow-pill.png") });

    await pill.click();
    await expect(pill).toHaveCount(0);
    await expect
      .poll(() => body.evaluate((el) => el.scrollHeight - el.scrollTop - el.clientHeight))
      .toBeLessThan(40);
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
