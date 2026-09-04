import { existsSync, mkdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, type Locator, type Page, test } from "@playwright/test";
import type { CommandInfo } from "../src/commands/types";
import { MockHost, mockSession } from "./mock-host";

// Drives the native ACP composer in a real browser against the mock host: it renders the structured agent
// pane from a pushed session-list, feeds it provider-neutral `agent-controls`, and exercises the three new
// features end to end — status line + live picker, `/` slash menu, and Up/Down prompt history — asserting both
// the rendered UI and the session-owned `agent.setControl` event the picker sends back. Screenshots land in
// .recordings for review.

const here = dirname(fileURLToPath(import.meta.url));
const distDir = join(here, "..", "dist");
const shotsDir = join(here, ".recordings", "acp-composer");

const agentSession = mockSession("cx", "acp", "acp");

const controls = {
  state: {
    axes: [
      {
        id: "model",
        label: "Model",
        description: "Model used for this session",
        category: "model",
        kind: "select",
        value: "gpt-5.5",
        valueLabel: "GPT-5.5",
        options: [
          { id: "gpt-5.5", label: "GPT-5.5", description: null },
          { id: "gpt-5.4-mini", label: "GPT-5.4 mini", description: null },
        ],
      },
      {
        id: "reasoning",
        label: "Reasoning",
        description: "Reasoning effort",
        category: "thought_level",
        kind: "select",
        value: "medium",
        valueLabel: "Medium",
        options: [
          { id: "low", label: "Low", description: "Fast responses." },
          { id: "medium", label: "Medium", description: "Balanced." },
          { id: "high", label: "High", description: "Deeper reasoning." },
        ],
      },
      {
        id: "fast",
        label: "Fast",
        description: "Use the provider's priority service tier",
        category: "model_config",
        kind: "boolean",
        value: "false",
        valueLabel: "Off",
        options: [
          { id: "true", label: "On", description: null },
          { id: "false", label: "Off", description: null },
        ],
      },
      {
        id: "mode",
        label: "Mode",
        description: null,
        category: "mode",
        kind: "select",
        value: "default",
        valueLabel: "Default",
        options: [
          { id: "plan", label: "Plan", description: null },
          { id: "default", label: "Default", description: null },
        ],
      },
    ],
    slash: [
      {
        id: "weavie:clear",
        name: "clear",
        description: "Clear the transcript and start a fresh conversation",
        kind: "weavieCommand",
        commandId: "weavie.agent.clearConversation",
        inputHint: null,
        inputName: null,
      },
      {
        id: "builtin:model",
        name: "model",
        description: "Switch the model, effort, or Fast Mode",
        kind: "weavieCommand",
        commandId: "weavie.agent.selectModel",
        inputHint: null,
        inputName: null,
      },
      {
        id: "builtin:plan",
        name: "plan",
        description: "Toggle Plan mode",
        kind: "weavieCommand",
        commandId: "weavie.agent.togglePlanMode",
        inputHint: null,
        inputName: null,
      },
      {
        id: "agent:compact",
        name: "compact",
        description: "Compact the conversation.",
        kind: "providerCommand",
        commandId: null,
        inputHint: null,
        inputName: null,
      },
      {
        id: "agent:review-pr",
        name: "review-pr",
        description: "Review a pull request.",
        kind: "providerCommand",
        commandId: null,
        inputHint: "<pull request>",
        inputName: null,
      },
    ],
  },
};

// The controls with Fast Mode on, as the host echoes them back after a toggle.
const fastOnControls = {
  ...controls,
  state: {
    ...controls.state,
    axes: controls.state.axes.map((axis) =>
      axis.id === "fast" ? { ...axis, value: "true", valueLabel: "On" } : axis,
    ),
  },
};

const planControls = {
  ...controls,
  state: {
    ...controls.state,
    axes: controls.state.axes.map((axis) =>
      axis.id === "mode" ? { ...axis, value: "plan", valueLabel: "Plan" } : axis,
    ),
  },
};

const paneMessage = (message: Record<string, unknown>) => ({
  providerId: "acp",
  ...message,
});

const userMessage = (text: string) => paneMessage({ type: "user-message", text });

const freeformQuestion = {
  id: "answer",
  header: "Answer",
  question: "What should ACP do?",
  allowsOther: false,
  kind: "string",
  required: true,
  format: null,
  initialValues: [],
  minimum: null,
  maximum: null,
  minimumLength: null,
  maximumLength: null,
  pattern: null,
  options: [],
};

const permissionActions = [
  { id: "allow-once", label: "Allow once", kind: "allow_once" },
  { id: "allow-always", label: "Always allow", kind: "allow_always" },
  { id: "reject-once", label: "Reject", kind: "reject_once" },
];

// The agent slice of the command catalog, as the host pushes it — the UI reads all key labels from here.
const agentCommand = (id: string, title: string, when: string, keys: string[]): CommandInfo => ({
  id,
  title,
  runsIn: "web",
  owner: "backend",
  executionLane: id === "weavie.agent.submit" ? "weavie.agent.input" : id,
  scope: "session",
  description: "",
  aliases: [],
  showInPalette: true,
  when,
  keys,
});

const approvalWhen = "agentFocused && agentApprovalPending";
const inputWhen = "agentFocused && agentInputPending";
const turnNavigationWhen = "agentFocused && agentTurnNavigable";
const catalog = {
  commands: [
    {
      ...agentCommand(
        "weavie.agent.clearConversation",
        "Start Fresh Agent Conversation",
        "agentFocused",
        ["alt+shift+c"],
      ),
      runsIn: "core" as const,
    },
    agentCommand("weavie.agent.submit", "Submit Agent Prompt", "agentComposerFocused", ["enter"]),
    agentCommand("weavie.agent.interrupt", "Interrupt Agent Turn", "agentFocused", ["escape"]),
    agentCommand("weavie.agent.jumpToTurn", "Jump to Agent Turn", turnNavigationWhen, ["alt+up"]),
    agentCommand("weavie.agent.jumpToLatest", "Jump to Latest Agent Activity", "agentFocused", [
      "alt+down",
    ]),
    agentCommand(
      "weavie.agent.toggleToolOutput",
      "Toggle Agent Tool Output",
      "agentFocused && agentToolOutputAvailable",
      ["alt+o"],
    ),
    agentCommand("weavie.agent.openPlan", "Open Agent Plan", "agentFocused", ["alt+p"]),
    agentCommand("weavie.agent.togglePlanMode", "Toggle Agent Plan Mode", "agentFocused", [
      "shift+tab",
    ]),
    agentCommand("weavie.agent.approve", "Approve Agent Request", approvalWhen, ["alt+y"]),
    agentCommand("weavie.agent.approveForSession", "Approve For Session", approvalWhen, [
      "alt+shift+y",
    ]),
    agentCommand("weavie.agent.decline", "Decline Agent Request", approvalWhen, ["alt+n"]),
    agentCommand("weavie.agent.declineInput", "Decline Agent Input Request", inputWhen, ["alt+n"]),
    agentCommand("weavie.agent.cancelInput", "Cancel Agent Input Request", inputWhen, [
      "alt+shift+n",
    ]),
    agentCommand("weavie.agent.acceptInput", "Submit Agent Input", inputWhen, ["alt+enter"]),
    agentCommand("weavie.pr.openCurrent", "Open Current Pull Request", "pullRequestAvailable", [
      "$mod+shift+g",
    ]),
    agentCommand("weavie.diff.againstHead", "Diff Against HEAD", "", []),
    agentCommand("weavie.review.open", "Review Changes", "", []),
  ],
  keybindings: [
    { key: "enter", command: "weavie.agent.submit", when: "agentComposerFocused" },
    { key: "escape", command: "weavie.agent.interrupt", when: "agentFocused" },
    { key: "alt+up", command: "weavie.agent.jumpToTurn", when: turnNavigationWhen },
    { key: "alt+down", command: "weavie.agent.jumpToLatest", when: "agentFocused" },
    {
      key: "alt+o",
      command: "weavie.agent.toggleToolOutput",
      when: "agentFocused && agentToolOutputAvailable",
    },
    { key: "alt+p", command: "weavie.agent.openPlan", when: "agentFocused" },
    { key: "shift+tab", command: "weavie.agent.togglePlanMode", when: "agentFocused" },
    { key: "alt+y", command: "weavie.agent.approve", when: approvalWhen },
    { key: "alt+shift+y", command: "weavie.agent.approveForSession", when: approvalWhen },
    { key: "alt+n", command: "weavie.agent.decline", when: approvalWhen },
    { key: "alt+n", command: "weavie.agent.declineInput", when: inputWhen },
    { key: "alt+shift+n", command: "weavie.agent.cancelInput", when: inputWhen },
    { key: "alt+enter", command: "weavie.agent.acceptInput", when: inputWhen },
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

test.describe("ACP composer", () => {
  let host: MockHost;

  test.beforeEach(async () => {
    host = await MockHost.start({ distDir, sessions: [agentSession] });
  });

  test.afterEach(async () => {
    await host.close();
  });

  const publishPane = (message: Record<string, unknown>): void =>
    host.publishAgentPane(agentSession.address, message);

  const publishControls = (value: typeof controls): void =>
    host.publishSession(agentSession.address, "agent", "controls", value);

  const publishCatalog = (): void => host.publishHost("commands", "catalog", catalog);

  const waitForAgentEvent = (name: string, after = 0) =>
    host.waitForSession(agentSession.address, "event", "agent", name, after);

  const waitForAgentPayload = async (name: string, after = 0): Promise<Record<string, unknown>> =>
    (await waitForAgentEvent(name, after)).payload as Record<string, unknown>;

  const lastAgentPayload = (name: string): Record<string, unknown> | undefined =>
    host.received
      .filter(
        (message) =>
          message.scope === "session" &&
          message.session?.incarnation === agentSession.address.incarnation &&
          message.kind === "event" &&
          message.feature === "agent" &&
          message.name === name,
      )
      .at(-1)?.payload as Record<string, unknown> | undefined;

  // Mounts the ACP session and its control surface after the exact-session hello creates its owner.
  async function mountAgent(page: Page): Promise<void> {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    const statusLine = page.locator(".agent-status-line");
    publishControls(controls);
    await expect(statusLine).toBeVisible();
  }

  // The app's own follow-to-bottom correction (AgentPaneScroll.onVirtualizerChange) chases convergence one
  // requestAnimationFrame at a time as the virtualizer keeps mounting/measuring rows, so how many real frames
  // it takes is unbounded under CI contention. Poll it frame-by-frame instead of racing it against a
  // wall-clock `expect.poll` timeout, which flaked here: 2026-09-03,
  // https://github.com/Kapps/weavie/actions/runs/33710321926/job/100508846088 ("Received: 47" after the full
  // Windows 30s expect.timeout — one row's height still short of settling).
  // Recurred on main CI 2026-09-04 04:51 UTC, same wait-vs-poll race, on both
  // e2e (linux) / shard (1/6) (https://github.com/Kapps/weavie/actions/runs/33838071761/job/100914906521,
  // "Received: 25") and e2e (macos) / shard (1/6)
  // (https://github.com/Kapps/weavie/actions/runs/33838071761/job/100914932032, "Received: 25") — the fix
  // below (PR #732) was open but not yet merged when this build ran; merging it applies the same fix here.
  async function waitForBottom(page: Page, body: Locator): Promise<void> {
    for (;;) {
      const distance = await body.evaluate(
        (element) => element.scrollHeight - element.scrollTop - element.clientHeight,
      );
      if (distance < 1) {
        return;
      }
      await page.evaluate(
        () => new Promise<void>((resolve) => requestAnimationFrame(() => resolve())),
      );
    }
  }

  async function revealScrollNavigation(page: Page): Promise<void> {
    const body = page.locator(".agent-body");
    const bounds = await body.boundingBox();
    if (bounds === null) {
      throw new Error("agent body has no viewport");
    }
    const clientWidth = await body.evaluate((element) => element.clientWidth);
    await page.mouse.move(bounds.x + clientWidth - 20, bounds.y + bounds.height / 2);
    await expect(page.locator(".agent-scroll-nav")).toHaveCSS("opacity", "1");
  }

  async function scrollNavigationIconVerticalOffset(page: Page, label: string): Promise<number> {
    return page.getByRole("button", { name: label, exact: true }).evaluate((button) => {
      const icon = button.querySelector("svg");
      if (icon === null) {
        return Number.POSITIVE_INFINITY;
      }
      const buttonBounds = button.getBoundingClientRect();
      const iconBounds = icon.getBoundingClientRect();
      return Math.abs(
        buttonBounds.top + buttonBounds.height / 2 - (iconBounds.top + iconBounds.height / 2),
      );
    });
  }

  test("status line shows each provider-owned ACP control", async ({ page }) => {
    await mountAgent(page);

    const segments = page.locator(".agent-status-segment");
    await expect(segments).toHaveCount(4);
    await expect(segments.nth(0)).toContainText("ModelGPT-5.5");
    await expect(segments.nth(1)).toContainText("ReasoningMedium");
    await expect(segments.nth(2)).toContainText("FastOff");
    await expect(segments.nth(3)).toContainText("ModeDefault");
    await page.screenshot({ path: join(shotsDir, "01-status-line.png") });
    await page.locator(".agent-compose").screenshot({ path: join(shotsDir, "00-compose-row.png") });
  });

  test("agent prose, code, composer, and chrome use the shared typography roles", async ({
    page,
  }) => {
    await mountAgent(page);
    publishPane(
      paneMessage({
        type: "item-completed",
        turnId: "t1",
        itemId: "answer-1",
        itemType: "agentMessage",
        status: "completed",
        text: "Rendered prose with `inline code`.",
      }),
    );
    host.publishHost("settings", "fonts", {
      editor: { family: '"Courier New", monospace', size: 21, weight: "700" },
      terminal: { family: "monospace", size: 13, weight: "normal" },
    });

    const styles = async (selector: string): Promise<Record<string, string>> =>
      page
        .locator(selector)
        .first()
        .evaluate((element) => {
          const style = getComputedStyle(element);
          return { family: style.fontFamily, size: style.fontSize, weight: style.fontWeight };
        });
    await expect(page.locator(".agent-markdown code")).toBeVisible();
    await expect
      .poll(async () => ({
        prose: await styles(".agent-markdown"),
        code: await styles(".agent-markdown code"),
        composer: await styles("[data-agent-composer] textarea"),
        chromeFamily: (await styles(".agent-status-line")).family,
      }))
      .toEqual({
        prose: { family: "Chivo, system-ui, sans-serif", size: "21px", weight: "400" },
        code: { family: '"Courier New", monospace', size: "21px", weight: "700" },
        composer: { family: '"Courier New", monospace', size: "21px", weight: "700" },
        chromeFamily: "Chivo, system-ui, sans-serif",
      });
  });

  test("expanded tool history keeps rich output beneath the full-width step label", async ({
    page,
  }) => {
    await mountAgent(page);
    publishCatalog();
    publishPane(userMessage("inspect the workspace"));
    for (const index of [1, 2]) {
      publishPane(
        paneMessage({
          type: "item-completed",
          turnId: "tool-history-turn",
          itemId: `tool-${index}`,
          itemType: "tool",
          category: "read",
          status: "completed",
          summary: `read file ${index}`,
          content: [
            {
              type: "text",
              text: `tool output ${index} ${"content ".repeat(40)}`,
            },
          ],
        }),
      );
    }

    const activity = page.locator(".agent-entry-activity", { hasText: "2 reads" });
    await activity.getByText("history 2", { exact: true }).click();
    const step = activity.locator(".agent-activity-step").first();
    await step.getByText("show output", { exact: true }).click();
    const geometry = await step.evaluate((element) => {
      const bounds = element.getBoundingClientRect();
      const label = element.querySelector(".agent-step-label")?.getBoundingClientRect();
      const rich = element.querySelector(".agent-entry-rich-content")?.getBoundingClientRect();
      if (label === undefined || rich === undefined) {
        throw new Error("Expanded history step is incomplete");
      }
      return {
        labelBottom: label.bottom,
        labelLeft: label.left,
        labelWidth: label.width,
        richLeft: rich.left,
        richRight: rich.right,
        richTop: rich.top,
        stepRight: bounds.right,
        stepWidth: bounds.width,
      };
    });

    expect(geometry.labelWidth).toBeGreaterThan(geometry.stepWidth / 2);
    expect(geometry.richTop).toBeGreaterThanOrEqual(geometry.labelBottom - 1);
    expect(Math.abs(geometry.richLeft - geometry.labelLeft)).toBeLessThanOrEqual(1);
    expect(Math.abs(geometry.richRight - geometry.stepRight)).toBeLessThanOrEqual(1);
  });

  test("expanded history hides long command output until the user asks for it", async ({
    page,
  }) => {
    await mountAgent(page);
    publishCatalog();
    publishPane(userMessage("run the noisy command"));
    publishPane(
      paneMessage({
        type: "item-completed",
        turnId: "command-output-turn",
        itemId: "command-output-1",
        itemType: "commandExecution",
        status: "completed",
        summary: "pnpm test --reporter verbose",
        text: `${"command output line\n".repeat(5_000)}COMMAND_OUTPUT_END`,
        content: [{ type: "text", text: "RICH_COMMAND_OUTPUT" }],
      }),
    );

    const activity = page.locator(".agent-entry-activity", { hasText: "ran 1 command" });
    await activity.getByText("history", { exact: true }).click();
    const step = activity.locator(".agent-activity-step");
    await expect(
      step.getByText("command pnpm test --reporter verbose", { exact: true }),
    ).toBeVisible();
    const disclosure = step.locator(".agent-tool-output-details");
    const toggle = disclosure.getByText("show output", { exact: true });
    await expect(toggle).toHaveAttribute("title", "Show tool output (Alt+O)");
    await expect(disclosure.locator(".agent-tool-output")).toHaveCount(0);
    await expect(step).not.toContainText("COMMAND_OUTPUT_END");
    await expect(step).not.toContainText("RICH_COMMAND_OUTPUT");

    await toggle.click();
    const output = disclosure.locator(".agent-tool-output");
    await expect(output).toBeVisible();
    await expect(output).toContainText("COMMAND_OUTPUT_END");
    await expect(output).toContainText("RICH_COMMAND_OUTPUT");
    const hide = disclosure.getByText("hide output", { exact: true });
    await expect(hide).toHaveAttribute("title", "Hide tool output (Alt+O)");

    await hide.focus();
    await page.keyboard.press("Alt+O");
    await expect(disclosure.locator(".agent-tool-output")).toHaveCount(0);

    publishPane(
      paneMessage({
        type: "item-completed",
        turnId: "command-output-turn",
        itemId: "later-answer",
        itemType: "agentMessage",
        status: "completed",
        text: `${"later response line\n".repeat(500)}LATER_RESPONSE_END`,
      }),
    );
    await expect(page.getByText("LATER_RESPONSE_END", { exact: false })).toBeAttached();
    const composer = page.locator("[data-agent-composer] textarea");
    await composer.focus();
    await page.keyboard.press("Alt+ArrowDown");
    await expect(disclosure).toHaveCount(1);
    await expect
      .poll(async () => {
        const body = await page.locator(".agent-body").boundingBox();
        const command = await disclosure.boundingBox();
        return body !== null && command !== null && command.y + command.height <= body.y;
      })
      .toBe(true);
    await composer.focus();
    await page.keyboard.press("Alt+O");
    await expect(disclosure.locator(".agent-tool-output")).toHaveCount(0);
  });

  test("expanded history hides read and file-change output behind the same reveal", async ({
    page,
  }) => {
    await mountAgent(page);
    publishCatalog();
    publishPane(userMessage("look at the file"));
    publishPane(
      paneMessage({
        type: "item-completed",
        turnId: "tool-output-turn",
        itemId: "read-1",
        itemType: "tool",
        category: "read",
        status: "completed",
        summary: "src/App.tsx",
        text: `${"file line\n".repeat(2_000)}READ_OUTPUT_END`,
      }),
    );
    publishPane(
      paneMessage({
        type: "item-completed",
        turnId: "tool-output-turn",
        itemId: "write-1",
        itemType: "fileChange",
        status: "completed",
        summary: "src/App.tsx",
        text: `${"written line\n".repeat(2_000)}WRITE_OUTPUT_END`,
      }),
    );

    const activity = page.locator(".agent-entry-activity");
    await activity.getByText("history 2", { exact: true }).click();
    const read = activity.locator(".agent-activity-step", { hasText: "read src/App.tsx" });
    const write = activity.locator(".agent-activity-step", { hasText: "edit src/App.tsx" });
    await expect(read.locator(".agent-tool-output")).toHaveCount(0);
    await expect(write.locator(".agent-tool-output")).toHaveCount(0);
    await expect(activity).not.toContainText("READ_OUTPUT_END");
    await expect(activity).not.toContainText("WRITE_OUTPUT_END");

    await read.getByText("show output", { exact: true }).click();
    await expect(read.locator(".agent-tool-output")).toContainText("READ_OUTPUT_END");
    await expect(write.locator(".agent-tool-output")).toHaveCount(0);
  });

  test("a failed step shows why it failed without a click", async ({ page }) => {
    await mountAgent(page);
    publishCatalog();
    publishPane(userMessage("run the failing command"));
    publishPane(
      paneMessage({
        type: "item-completed",
        turnId: "failed-turn",
        itemId: "failed-1",
        itemType: "commandExecution",
        status: "failed",
        summary: "pnpm test",
        text: "FAILURE_REASON",
      }),
    );

    const activity = page.locator(".agent-entry-activity");
    await activity.getByText("history", { exact: true }).click();
    const step = activity.locator(".agent-activity-step");
    await expect(step.locator(".agent-tool-output")).toContainText("FAILURE_REASON");
    await step.getByText("hide output", { exact: true }).click();
    await expect(step.locator(".agent-tool-output")).toHaveCount(0);
  });

  test("mouse clicks return to the prompt without taking text selection or response-field focus", async ({
    page,
  }) => {
    await mountAgent(page);

    const textarea = page.locator("[data-agent-composer] textarea");
    const run = page.locator("[data-agent-composer] button[type='submit']");
    await expect(run).toBeDisabled();
    const model = page.locator(".agent-status-segment", { hasText: "Model" });
    await model.focus();
    await run.click({ force: true });
    await expect(textarea).toBeFocused();

    await textarea.fill("ready");
    await page.locator(".agent-surface .pane-head").click();
    await expect(textarea).toBeFocused();
    await page.keyboard.type(" after chrome");

    await model.click();
    await expect(page.locator(".agent-control-picker")).toBeVisible();
    await expect(textarea).toBeFocused();
    await page.keyboard.type(" and a button");
    await expect(textarea).toHaveValue("ready after chrome and a button");
    await page.keyboard.press("Escape");
    await textarea.fill("");

    publishPane(userMessage("selectable"));
    const transcript = page.locator(".agent-entry-text", { hasText: "selectable" });
    const box = await transcript.boundingBox();
    if (box === null) {
      throw new Error("selectable transcript text has no layout box");
    }
    const selectionY = box.y + box.height / 2;
    await page.mouse.move(box.x, selectionY);
    await page.mouse.down();
    await page.mouse.move(box.x + 100, selectionY, { steps: 5 });
    await page.mouse.up();
    await expect
      .poll(() => page.evaluate(() => document.getSelection()?.toString()))
      .toContain("selectable");

    await page.evaluate(() => document.getSelection()?.removeAllRanges());
    const runBox = await run.boundingBox();
    if (runBox === null) {
      throw new Error("disabled Run button has no layout box");
    }
    await page.mouse.move(box.x, selectionY);
    await page.mouse.down();
    await page.mouse.move(runBox.x + runBox.width / 2, runBox.y + runBox.height / 2, { steps: 5 });
    await page.mouse.up();
    await expect
      .poll(() => page.evaluate(() => document.getSelection()?.toString()))
      .toContain("selectable");

    publishPane(
      paneMessage({
        type: "input-requested",
        itemId: "input-1",
        requestId: "input-1",
        status: "pending",
        questions: [freeformQuestion],
      }),
    );
    const response = page.locator(".agent-input-request input");
    await response.click();
    await expect(response).toBeFocused();
    await response.fill("Keep this field focused");
    await expect(response).toHaveValue("Keep this field focused");
  });

  test("an input request stays docked while later updates scroll beneath it", async ({ page }) => {
    await mountAgent(page);
    await page.setViewportSize({ width: 800, height: 500 });
    publishPane(
      paneMessage({
        type: "input-requested",
        itemId: "input-draft",
        requestId: "input-draft",
        status: "pending",
        questions: [freeformQuestion],
      }),
    );
    const dock = page.locator("[data-agent-pending-request]");
    const response = dock.locator(".agent-input-request input");
    const body = page.locator(".agent-body");
    await expect(dock).toBeVisible();
    await expect(page.locator(".agent-empty")).toHaveCount(0);
    await expect(page.locator(".agent-body .agent-input-request")).toHaveCount(0);
    await response.fill("Keep this answer");
    await expect(response).toBeFocused();
    const initialDockHeight = await dock.evaluate((element) =>
      Math.round(element.getBoundingClientRect().height),
    );

    for (let index = 0; index < 60; index += 1) {
      publishPane(
        paneMessage({
          type: "item-completed",
          itemId: `later-update-${index}`,
          itemType: "agentMessage",
          status: "completed",
          text: `Later agent update ${index}\nwith enough text to move the request off screen`,
        }),
      );
    }
    await body.evaluate((element) => {
      element.scrollTop = element.scrollHeight;
    });
    await expect(dock).toBeVisible();
    await expect(response).toBeFocused();
    await expect(response).toHaveValue("Keep this answer");
    await expect
      .poll(() => dock.evaluate((element) => Math.round(element.getBoundingClientRect().height)))
      .toBe(initialDockHeight);
    await expect
      .poll(() =>
        dock.evaluate((element) => {
          const controls = [
            element.querySelector(".agent-input-request input"),
            element.querySelector(".agent-input-request button[type='submit']"),
          ];
          return controls.every((control) => {
            const bounds = control?.getBoundingClientRect();
            if (bounds === undefined) {
              return false;
            }
            const hit = document.elementFromPoint(
              bounds.left + bounds.width / 2,
              bounds.top + bounds.height / 2,
            );
            return hit === control || control?.contains(hit);
          });
        }),
      )
      .toBe(true);
    await expect
      .poll(() =>
        dock.evaluate(
          (element) =>
            element.getBoundingClientRect().bottom -
            (element.nextElementSibling?.getBoundingClientRect().top ?? Number.NaN),
        ),
      )
      .toBe(0);
    await waitForBottom(page, body);

    publishPane(paneMessage({ type: "input-resolved", itemId: "input-draft", status: "resolved" }));
    await expect(dock).toHaveCount(0);
    await waitForBottom(page, body);
    await expect(page.getByRole("button", { name: "↓ Jump to latest", exact: true })).toHaveCount(
      0,
    );
    await body.evaluate((element) => {
      element.scrollTop = 0;
    });
    const resolved = page.locator(".agent-entry-request");
    await expect(resolved.locator(".agent-entry-status")).toHaveText("resolved");
    await expect(resolved.locator(".agent-input-request")).toHaveCount(0);
  });

  test("a multi-select input returns advertised and custom answers", async ({ page }) => {
    await mountAgent(page);
    publishPane(
      paneMessage({
        type: "input-requested",
        itemId: "input-multiple",
        requestId: "input-multiple",
        status: "pending",
        questions: [
          {
            ...freeformQuestion,
            id: "choices",
            header: "Choices",
            question: "Choose one or more values",
            kind: "array",
            allowsOther: true,
            minimumLength: 1,
            options: [
              { value: "one", label: "One", description: "First choice." },
              { value: "two", label: "Two", description: "Second choice." },
            ],
          },
        ],
      }),
    );

    const request = page.locator(".agent-input-request");
    await request.locator("select").selectOption(["one", "__weavie_custom_answer__"]);
    await request.locator('input[placeholder="Type another answer"]').fill("custom");
    await request.getByRole("button", { name: "Submit answers" }).click();

    expect(await waitForAgentPayload("input")).toMatchObject({
      requestId: "input-multiple",
      answers: { choices: ["one", "custom"] },
    });
  });

  test("free-form arrays and untouched booleans return typed form values", async ({ page }) => {
    await mountAgent(page);
    publishPane(
      paneMessage({
        type: "input-requested",
        itemId: "input-array",
        requestId: "input-array",
        status: "pending",
        questions: [
          {
            ...freeformQuestion,
            id: "paths",
            kind: "array",
            required: true,
            minimumLength: 2,
            maximumLength: 3,
          },
        ],
      }),
    );

    const form = page.locator(".agent-input-request");
    const array = form.locator('textarea[placeholder="One value per line"]');
    await array.fill("src/one.ts");
    await form.getByRole("button", { name: "Submit answers" }).click();
    await expect(array).toBeFocused();
    expect(await array.evaluate((input) => input.validationMessage)).toContain("at least 2 values");

    await array.fill("src/one.ts\nsrc/two.ts");
    await form.getByRole("button", { name: "Submit answers" }).click();
    expect(await waitForAgentPayload("input")).toMatchObject({
      requestId: "input-array",
      action: "accept",
      answers: { paths: ["src/one.ts", "src/two.ts"] },
    });
    publishPane(paneMessage({ type: "input-resolved", itemId: "input-array", status: "accepted" }));

    publishPane(
      paneMessage({
        type: "input-requested",
        itemId: "input-boolean",
        requestId: "input-boolean",
        status: "pending",
        questions: [
          {
            ...freeformQuestion,
            id: "enabled",
            kind: "boolean",
            required: true,
          },
        ],
      }),
    );
    const beforeBoolean = host.received.length;
    await page
      .locator(".agent-input-request")
      .getByRole("button", { name: "Submit answers" })
      .click();
    expect(await waitForAgentPayload("input", beforeBoolean)).toMatchObject({
      requestId: "input-boolean",
      action: "accept",
      answers: { enabled: ["false"] },
    });
  });

  test("the input shortcut honors native form validation", async ({ page }) => {
    await mountAgent(page);
    publishCatalog();
    publishPane(
      paneMessage({
        type: "input-requested",
        itemId: "input-validation",
        requestId: "input-validation",
        status: "pending",
        questions: [{ ...freeformQuestion, pattern: "^ok$" }],
      }),
    );

    const input = page.locator(".agent-input-request input");
    await input.fill("invalid");
    const beforeInvalid = host.received.length;
    await page.locator("[data-agent-composer] textarea").click();
    await page.keyboard.press("Alt+Enter");
    await expect(input).toBeFocused();
    expect(await input.evaluate((element) => element.validationMessage)).not.toBe("");
    expect(lastAgentPayload("input")).toBeUndefined();

    await input.fill("ok");
    await page.locator("[data-agent-composer] textarea").click();
    await page.keyboard.press("Alt+Enter");
    expect(await waitForAgentPayload("input", beforeInvalid)).toMatchObject({
      requestId: "input-validation",
      action: "accept",
      answers: { answer: ["ok"] },
    });
  });

  test("form and URL requests expose distinct decline and cancel actions", async ({ page }) => {
    await mountAgent(page);
    publishCatalog();
    publishPane(
      paneMessage({
        type: "input-requested",
        itemId: "form-decline",
        requestId: "form-decline",
        itemType: "elicitation",
        status: "pending",
        questions: [freeformQuestion],
      }),
    );

    const form = page.locator("[data-agent-pending-request]");
    await expect(form.getByRole("button", { name: "Decline", exact: true })).toHaveAttribute(
      "title",
      "Decline request (Alt+N)",
    );
    await expect(form.getByRole("button", { name: "Cancel", exact: true })).toHaveAttribute(
      "title",
      "Cancel request (Alt+Shift+N)",
    );
    await page.locator("[data-agent-composer] textarea").click();
    const formDecision = waitForAgentPayload("input");
    await page.keyboard.press("Alt+n");
    expect(await formDecision).toMatchObject({
      requestId: "form-decline",
      action: "decline",
      answers: {},
    });
    publishPane(paneMessage({ type: "input-resolved", itemId: "form-decline", status: "decline" }));

    publishPane(
      paneMessage({
        type: "input-requested",
        itemId: "url-decline",
        requestId: "url-decline",
        itemType: "url",
        resourceUri: "https://example.test/login",
        status: "pending",
      }),
    );
    const url = page.locator("[data-agent-pending-request]");
    await expect(url.getByText("https://example.test/login", { exact: true })).toBeVisible();
    await expect(url.getByRole("link", { name: "https://example.test/login" })).toHaveCount(0);
    const decline = url.getByRole("button", { name: "Decline", exact: true });
    await expect(decline).toHaveAttribute("title", "Decline request (Alt+N)");
    await expect(url.getByRole("button", { name: "Cancel", exact: true })).toHaveAttribute(
      "title",
      "Cancel request (Alt+Shift+N)",
    );
    await page.locator("[data-agent-composer] textarea").click();
    const urlDecision = waitForAgentPayload("input", host.received.length);
    await page.keyboard.press("Alt+Shift+n");
    expect(await urlDecision).toMatchObject({
      requestId: "url-decline",
      action: "cancel",
      answers: {},
    });
  });

  test("status line shows Git's HEAD diff instead of the review aggregation", async ({ page }) => {
    await mountAgent(page);
    publishCatalog();
    host.files.set("/workspace/one.ts", "export const one = true;\n");
    host.publishSession(agentSession.address, "review", "changes", {
      label: "",
      files: [
        { path: "/workspace/one.ts", name: "one.ts", added: 7, removed: 1, line: 2 },
        { path: "/workspace/two.ts", name: "two.ts", added: 5, removed: 3, line: 4 },
      ],
    });
    host.publishSession(agentSession.address, "git", "status", {
      branch: "main",
      dirty: true,
      added: 3,
      removed: 8,
      error: null,
    });

    const counts = page.locator(".agent-status-diff");
    await expect(counts).toHaveText("+3/-8");
    await expect(counts).toHaveAttribute(
      "title",
      "Review diff against HEAD — 3 lines added, 8 removed",
    );
    const [chipBox, statusBox] = await Promise.all([
      counts.boundingBox(),
      page.locator(".agent-status-line").boundingBox(),
    ]);
    expect(chipBox).not.toBeNull();
    expect(statusBox).not.toBeNull();
    expect((chipBox?.x ?? 0) + (chipBox?.width ?? 0)).toBeLessThanOrEqual(
      (statusBox?.x ?? 0) + (statusBox?.width ?? 0),
    );
    await counts.click();
    const diffAgainst = await host.waitForSession(
      agentSession.address,
      "event",
      "review",
      "diffAgainst",
    );
    expect(diffAgainst.payload).toMatchObject({ reference: "HEAD" });

    host.publishSession(agentSession.address, "review", "changes", { label: "", files: [] });
    await expect(counts).toHaveText("+3/-8");
    host.publishSession(agentSession.address, "git", "status", {
      branch: "main",
      dirty: false,
      added: 0,
      removed: 0,
      error: null,
    });
    await expect(counts).toHaveCount(0);
  });

  test("Shift+Tab and /plan share the Plan-mode command and advertise its binding", async ({
    page,
  }) => {
    await mountAgent(page);
    publishCatalog();

    const mode = page.locator(".agent-status-segment", { hasText: "Default" });
    await expect(mode).toHaveAttribute("title", /Shift\+Tab/);
    const textarea = page.locator("[data-agent-composer] textarea");
    await textarea.click();
    await page.keyboard.press("Shift+Tab");
    expect(await waitForAgentPayload("setControl")).toMatchObject({
      axis: "mode",
      value: "plan",
    });

    publishControls(planControls);
    await expect(page.locator(".agent-status-segment", { hasText: "Plan" })).toBeVisible();
    await textarea.fill("/plan");
    const row = page.locator(".agent-slash-option", { hasText: "/plan" });
    await expect(row).toContainText("Shift+Tab");
    await page.screenshot({ path: join(shotsDir, "13-plan-mode.png") });
    await page.keyboard.press("Enter");
    await expect.poll(() => lastAgentPayload("setControl")?.value).toBe("default");
  });

  test("a completed plan opens as a read-only editor document", async ({ page }) => {
    await mountAgent(page);
    publishCatalog();
    publishPane(
      paneMessage({
        type: "item-completed",
        threadId: "thread-plan",
        turnId: "turn-plan",
        itemId: "plan-1",
        itemType: "plan",
        text: "# Implementation\n\n1. Add the plan document.",
        status: "completed",
      }),
    );

    const card = page.locator(".agent-entry-plan");
    await expect(card).toContainText("Ready to review in the editor");
    await expect(card).not.toContainText("Implementation");
    const open = card.getByRole("button", { name: "Open plan" });
    await expect(open).toHaveAttribute("title", "Open plan in editor (Alt+P)");
    const planPath = "agent-plan:cx:thread-plan:turn-plan:plan-1";
    const requestOpen = async (after: number): Promise<void> => {
      await open.click();
      const request = await host.waitForSession(
        agentSession.address,
        "request",
        "agent",
        "openPlan",
        after,
      );
      expect(request.payload).toMatchObject({
        threadId: "thread-plan",
        turnId: "turn-plan",
        itemId: "plan-1",
      });
      host.respond(request, true);
    };
    const publishPlan = (): void => {
      host.publishSession(agentSession.address, "editor", "agentPlan", {
        id: "cx:thread-plan:turn-plan:plan-1",
        path: planPath,
        title: "Implementation plan",
        markdown:
          "# Implementation\n\n1. Add the plan document.\n\n" +
          "```mermaid\nflowchart LR\n  A[Plan] --> B[Ship]\n```",
      });
    };
    const openPlan = (): void => {
      host.publishSession(agentSession.address, "editor", "openOverlay", {
        path: planPath,
        kind: "plan",
      });
    };

    await requestOpen(host.checkpoint());
    publishPlan();
    openPlan();

    const plan = page.locator(".editor-plan");
    await expect(plan).toBeVisible();
    await expect(plan.locator(".editor-plan-head h1")).toHaveText("Implementation plan");
    await expect(plan.locator(".agent-markdown")).toContainText("Add the plan document.");
    await expect(plan.locator(".mermaid-rendered > svg")).toBeVisible();
    await expect(page.locator(".editor-tab", { hasText: "Implementation plan" })).toBeVisible();
    await page.screenshot({ path: join(shotsDir, "14-plan-document.png") });

    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByRole("button", { name: "Agent", exact: true }).click();
    await requestOpen(host.checkpoint());
    await expect(page.locator(".mobile-surface-button.active")).toHaveText("Agent");
    publishPlan();
    await expect(page.locator(".mobile-surface-button.active")).toHaveText("Agent");
    openPlan();
    await expect(page.locator(".mobile-surface-button.active")).toHaveText("Code");
    await expect(plan).toBeVisible();
  });

  // The order a reconnect resync replays in: the editor session (reopening the plan tab) lands before the plan
  // document, so the pane mounts against an empty store and must still render once the content arrives.
  test("a plan tab restored before its document renders when the document replays", async ({
    page,
  }) => {
    await mountAgent(page);
    publishCatalog();
    const planPath = "agent-plan:cx:thread-plan:turn-plan:plan-1";

    host.publishSession(agentSession.address, "editor", "restore", {
      session: {
        active: planPath,
        open: [{ path: planPath, kind: "plan", viewState: null }],
      },
    });
    await expect(page.locator(".editor-plan")).toBeVisible();

    host.publishSession(agentSession.address, "editor", "agentPlan", {
      id: "cx:thread-plan:turn-plan:plan-1",
      path: planPath,
      title: "Implementation plan",
      markdown: "# Implementation\n\n1. Add the plan document.",
    });

    await expect(page.locator(".editor-plan-head h1")).toHaveText("Implementation plan");
    await expect(page.locator(".agent-markdown")).toContainText("Add the plan document.");
    await expect(page.locator(".editor-tab", { hasText: "Implementation plan" })).toBeVisible();
  });

  test("Alt+P explains when no completed plan is available", async ({ page }) => {
    await mountAgent(page);
    publishCatalog();

    await expect(page.locator(".agent-status-segment", { hasText: "Default" })).toHaveAttribute(
      "title",
      /Shift\+Tab/,
    );
    await page.locator("[data-agent-composer] textarea").click();
    await page.keyboard.press("Alt+p");
    await expect(
      page.locator(".toast", { hasText: "No completed plan is available yet." }),
    ).toBeVisible();
  });

  test("status line links the current branch's pull request", async ({ page }) => {
    await mountAgent(page);
    const url = `${host.url}/pull/123`;
    publishCatalog();
    host.publishSession(agentSession.address, "git", "status", {
      branch: "feat/native-ui-pr",
      dirty: false,
    });
    host.publishSession(agentSession.address, "git", "pullRequest", {
      branch: "feat/native-ui-pr",
      pullRequest: { number: 123, url, state: "open" },
      error: null,
    });

    const link = page.locator(".agent-status-pr");
    await expect(link).toHaveText("#123");
    const modifier = process.platform === "darwin" ? "⌘" : "Ctrl";
    await expect(link).toHaveAttribute("title", new RegExp(`${modifier}\\+Shift\\+G`));
    const popupPromise = page.waitForEvent("popup");
    await link.click();
    await expect(await popupPromise).toHaveURL(url);

    host.publishSession(agentSession.address, "git", "pullRequest", {
      branch: "feat/native-ui-pr",
      pullRequest: { number: 123, url, state: "merged" },
      error: "temporary network failure",
    });
    await expect(link).toHaveText("#123 · Merged");
    await expect(link).toHaveAttribute("title", /last refresh failed: temporary network failure/);
    await expect(page.locator(".agent-status-unavailable")).toHaveCount(0);

    host.publishSession(agentSession.address, "git", "status", {
      branch: "another-branch",
      dirty: false,
    });
    await expect(link).toHaveCount(0);
  });

  test("subagent completion keeps the primary turn working until its own result", async ({
    page,
  }) => {
    await mountAgent(page);
    publishPane(
      paneMessage({
        type: "user-message",
        threadId: "thread-primary",
        text: "Do the work",
      }),
    );
    publishPane(
      paneMessage({
        type: "turn-started",
        threadId: "thread-primary",
        turnId: "turn-primary",
        isPrimaryThread: true,
      }),
    );
    publishPane(
      paneMessage({
        type: "turn-started",
        threadId: "thread-subagent",
        turnId: "turn-subagent",
        isPrimaryThread: false,
      }),
    );
    publishPane(
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
    publishPane(
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

    publishPane(
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
    publishPane(
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

  test("the model picker switches the provider-owned model axis", async ({ page }) => {
    await mountAgent(page);

    await page.locator(".agent-status-segment", { hasText: "Model" }).click();
    const picker = page.locator(".agent-control-picker");
    await expect(picker).toBeVisible();
    await expect(picker.locator(".agent-control-option")).toHaveCount(2);
    await page.screenshot({ path: join(shotsDir, "02-model-picker.png") });

    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("Enter");

    const set = await waitForAgentPayload("setControl");
    expect(set).toMatchObject({ axis: "model", value: "gpt-5.4-mini" });
    await expect(picker).toBeHidden();
  });

  test("a control picker is dismissed by clicking away or its own segment", async ({ page }) => {
    await mountAgent(page);

    const model = page.locator(".agent-status-segment", { hasText: "Model" });
    const picker = page.locator(".agent-control-picker");
    await model.click();
    await expect(picker).toBeVisible();

    // Clicking anywhere outside closes it — without applying an option.
    await page.locator(".agent-body").click();
    await expect(picker).toBeHidden();
    expect(lastAgentPayload("setControl")).toBeUndefined();

    // The segment that opened it closes it again.
    await model.click();
    await expect(picker).toBeVisible();
    await model.click();
    await expect(picker).toBeHidden();

    // Another axis's segment switches the picker instead of leaving the first one open.
    await model.click();
    await page.locator(".agent-status-segment", { hasText: "Fast" }).click();
    await expect(picker).toHaveCount(1);
    await expect(picker).toContainText("Fast");
    expect(lastAgentPayload("setControl")).toBeUndefined();
  });

  test("the reasoning picker applies the provider-owned thought level", async ({ page }) => {
    await mountAgent(page);

    await page.locator(".agent-status-segment", { hasText: "Reasoning" }).click();
    const sub = page.locator(".agent-control-picker .agent-control-option");
    await expect(sub).toHaveCount(3);
    await page.screenshot({ path: join(shotsDir, "02b-effort-submenu.png") });

    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("Enter");

    const set = await waitForAgentPayload("setControl");
    expect(set).toMatchObject({ axis: "reasoning", value: "high" });
    await expect(page.locator(".agent-control-picker")).toBeHidden();
  });

  test("the Fast picker toggles the provider-owned boolean axis", async ({ page }) => {
    await mountAgent(page);

    await page.locator(".agent-status-segment", { hasText: "Fast" }).click();
    const fastItems = page.locator(".agent-control-picker .agent-control-option");
    await expect(fastItems).toHaveCount(2);
    await fastItems.filter({ hasText: "On" }).click();

    const set = await waitForAgentPayload("setControl");
    expect(set).toMatchObject({ axis: "fast", value: "true" });

    publishControls(fastOnControls);
    await expect(page.locator(".agent-status-segment", { hasText: "Fast" })).toContainText("On");
    await page.screenshot({ path: join(shotsDir, "12-fast-on.png") });
  });

  test("keyboard focus in a control picker survives a host re-push", async ({ page }) => {
    await mountAgent(page);

    await page.locator(".agent-status-segment", { hasText: "Reasoning" }).click();
    await page.keyboard.press("ArrowDown");
    const high = page.locator(".agent-control-option", { hasText: "High" });
    await expect(high).toHaveClass(/active/);

    publishControls(controls);
    await expect(high).toHaveClass(/active/);
  });

  test("the model picker keeps its keyboard highlight across a host re-push", async ({ page }) => {
    await mountAgent(page);

    await page.locator(".agent-status-segment", { hasText: "Model" }).click();
    const options = page.locator(".agent-control-picker .agent-control-option");
    await expect(options.nth(0)).toHaveClass(/active/); // seeded on the current value
    await page.keyboard.press("ArrowDown");
    await expect(options.nth(1)).toHaveClass(/active/);

    // A control re-push (which every SetControl triggers) must not re-seed the highlight mid-use.
    publishControls(controls);
    await expect(options.nth(1)).toHaveClass(/active/);

    // Keyboard selection still applies the highlighted option after the re-push.
    await page.keyboard.press("Enter");
    const set = await waitForAgentPayload("setControl");
    expect(set).toMatchObject({ axis: "model", value: "gpt-5.4-mini" });
  });

  test("typing / opens the slash menu and inserts a provider command", async ({ page }) => {
    await mountAgent(page);

    const textarea = page.locator("[data-agent-composer] textarea");
    await textarea.click();
    await page.keyboard.type("/");

    const menu = page.locator(".agent-slash-menu");
    await expect(menu).toBeVisible();
    await expect(menu.locator(".agent-slash-option")).toHaveCount(5);
    await expect(menu).toContainText("/clear");
    await expect(menu).toContainText("/model");
    await expect(menu).toContainText("/plan");
    await expect(menu).toContainText("/review-pr");
    await page.screenshot({ path: join(shotsDir, "03-slash-menu.png") });

    await page.keyboard.type("rev");
    await expect(menu.locator(".agent-slash-option")).toHaveCount(1);
    await page.keyboard.press("Enter");
    await expect(menu).toBeHidden();
    await expect(textarea).toHaveValue("/review-pr ");
    await expect(textarea).toBeFocused();
    await page.screenshot({ path: join(shotsDir, "05-provider-command.png") });
  });

  test("a no-input provider command submits with ACP command semantics", async ({ page }) => {
    await mountAgent(page);

    const textarea = page.locator("[data-agent-composer] textarea");
    await textarea.fill("/compact");
    await page.keyboard.press("Enter");

    expect(await waitForAgentPayload("submit")).toMatchObject({
      prompt: "/compact",
      kind: "providerCommand",
      commandName: "compact",
      attachmentIds: [],
    });
  });

  test("a manually typed /clear dispatches the Weavie command instead of an agent prompt", async ({
    page,
  }) => {
    await mountAgent(page);
    publishCatalog();

    const before = host.received.length;
    const request = host.waitForSession(
      agentSession.address,
      "request",
      "commands",
      "invoke",
      before,
    );
    const textarea = page.locator("[data-agent-composer] textarea");
    await textarea.fill("/clear");
    await page.keyboard.press("Escape");
    await page.getByRole("button", { name: "Run" }).click();

    const invocation = await request;
    expect(invocation.payload).toMatchObject({ id: "weavie.agent.clearConversation" });
    expect(
      host.received
        .slice(before)
        .some((message) => message.feature === "agent" && message.name === "submit"),
    ).toBe(false);
    host.respond(invocation, { ok: true, message: "Started fresh.", error: null });
  });

  test("clicking outside the composer dismisses the slash menu", async ({ page }) => {
    await mountAgent(page);

    const textarea = page.locator("[data-agent-composer] textarea");
    await textarea.click();
    await page.keyboard.type("/");
    const menu = page.locator(".agent-slash-menu");
    await expect(menu).toBeVisible();

    // A click inside the composer is still the query's own surface; only leaving it dismisses.
    await textarea.click();
    await expect(menu).toBeVisible();
    await page.locator(".agent-body").click();
    await expect(menu).toBeHidden();
    await expect(textarea).toHaveValue("/");
  });

  // Pins the composer's turn-progress wiring: the working row (with elapsed time), the Run→Steer submit
  // relabel, the turn-only Interrupt button, and the amber waiting state while an approval is pending.
  test("the working row tracks the turn: working, waiting, back to working, gone", async ({
    page,
  }) => {
    await mountAgent(page);
    const working = page.locator(".agent-working");
    const submit = page.locator("[data-agent-composer] button[type=submit]");
    const interrupt = page.locator("[data-agent-composer] button", { hasText: "Interrupt" });

    // Idle: no row, no Interrupt button, submit reads Run.
    await expect(working).toHaveCount(0);
    await expect(interrupt).toHaveCount(0);
    await expect(submit).toHaveText("Run");

    publishPane(
      paneMessage({
        type: "turn-started",
        turnId: "t1",
        status: "inProgress",
        startedAtMs: Date.now() - 3_000,
      }),
    );
    await expect(working).toBeVisible();
    await expect(working.locator(".agent-working-label")).toHaveText("Working");
    await expect(working.locator(".agent-working-time")).toHaveText(/^\d+s$/);
    await expect(submit).toHaveText("Steer");
    await expect(interrupt).toBeVisible();
    await page.screenshot({ path: join(shotsDir, "06-working-row.png") });

    publishPane(
      paneMessage({
        type: "approval-requested",
        itemId: "a1",
        requestId: "a1",
        status: "pending",
        summary: "Run: dotnet test",
        actions: permissionActions,
      }),
    );
    await expect(working).toHaveClass(/waiting/);
    await expect(working.locator(".agent-working-label")).toHaveText("Waiting on your approval");
    await page.screenshot({ path: join(shotsDir, "07-waiting-row.png") });

    publishPane(paneMessage({ type: "approval-resolved", itemId: "a1", status: "accept" }));
    await expect(working).not.toHaveClass(/waiting/);
    await expect(working.locator(".agent-working-label")).toHaveText("Working");

    publishPane(paneMessage({ type: "turn-completed", turnId: "t1", status: "interrupted" }));
    await expect(working).toHaveCount(0);
    await expect(submit).toHaveText("Run");
    await expect(interrupt).toHaveCount(0);
  });

  // The elapsed clock is anchored to the provider's persisted turn time, so leaving a mid-turn session and
  // coming back keeps it counting real wall-clock instead of restarting near zero.
  test("the working timer keeps counting across a session switch — it never resets", async ({
    page,
  }) => {
    await mountAgent(page);
    const secondSession = mockSession("cx2", "other", "acp");
    host.setSessions([agentSession, secondSession]);
    await expect(page.locator(".session-chip")).toHaveCount(2);
    host.publishSession(secondSession.address, "agent", "controls", controls);
    const working = page.locator(".agent-working");
    const timeText = working.locator(".agent-working-time");
    const readSeconds = async (): Promise<number> => {
      const text = (await timeText.textContent()) ?? "";
      const match = text.match(/(?:(\d+)m\s*)?(\d+)s/);
      return match === null ? -1 : (match[1] ? Number(match[1]) * 60 : 0) + Number(match[2]);
    };

    // Start a turn on the ACP session; let its timer tick past a couple of seconds so a reset would be stark.
    publishPane(
      paneMessage({
        type: "turn-started",
        turnId: "t1",
        status: "inProgress",
        startedAtMs: Date.now() - 3_000,
      }),
    );
    await expect(working).toBeVisible();
    await expect.poll(readSeconds, { timeout: 8_000 }).toBeGreaterThanOrEqual(2);
    const before = await readSeconds();

    // Switch to a different session (no active turn) — the ACP working row leaves with it.
    await page.locator('.session-chip[title^="other —"]').click();
    await expect(working).toHaveCount(0);

    // Sit on the other session for several wall-clock seconds, then return to the still-running ACP turn.
    await page.waitForTimeout(4_000);
    await page.locator('.session-chip[title^="acp —"]').click();
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
    await mountAgent(page);
    publishCatalog();

    const empty = page.locator(".agent-empty");
    await expect(empty).toBeVisible();
    await expect(empty.locator(".agent-empty-title")).toHaveText("ACP");
    await expect(empty.locator("kbd")).toHaveText(["Enter", "/", "↑", "Escape"]);
    await expect(page.locator("[data-agent-composer] textarea")).toHaveAttribute(
      "placeholder",
      "Write a prompt — / for commands and skills",
    );
    await expect(page.locator("[data-agent-composer]")).not.toContainText("prompt>");
    await page.screenshot({ path: join(shotsDir, "08-empty-state.png") });
  });

  // Pins the informed-approval flow: the card shows the command under review, the buttons wear their
  // chords, and Alt+Y answers the pending request from the keyboard.
  test("an approval card shows the command and answers to Alt+Y", async ({ page }) => {
    await mountAgent(page);
    publishCatalog();
    publishPane(paneMessage({ type: "turn-started", turnId: "t1", status: "inProgress" }));
    publishPane(
      paneMessage({
        type: "approval-requested",
        itemId: "a1",
        requestId: "a1",
        status: "pending",
        summary: "Wants to run the test suite.",
        text: "dotnet test tests/Weavie.Hosting.Tests",
        actions: permissionActions,
      }),
    );

    const card = page.locator(".agent-entry-request");
    await expect(card).toContainText("dotnet test tests/Weavie.Hosting.Tests");
    const accept = card.getByRole("button", { name: /Allow once/ });
    await expect(accept.locator(".agent-key-chip")).toHaveText("Alt+Y");
    await page.screenshot({ path: join(shotsDir, "09-approval-card.png") });

    await page.locator("[data-agent-composer] textarea").click();
    await page.keyboard.press("Alt+y");
    const decision = await waitForAgentPayload("permission");
    expect(decision).toMatchObject({ requestId: "a1", optionId: "allow-once" });
  });

  // Regression: once the approval resolves, its decision buttons must go — the card is no longer actionable.
  // The header status flips reactively; the buttons must flip with it in the same live update (no re-mount).
  test("a resolved approval drops its decision buttons in place", async ({ page }) => {
    await mountAgent(page);
    publishCatalog();
    publishPane(paneMessage({ type: "turn-started", turnId: "t1", status: "inProgress" }));
    publishPane(
      paneMessage({
        type: "approval-requested",
        itemId: "a1",
        requestId: "a1",
        status: "pending",
        summary: "Wants to run the test suite.",
        text: "dotnet test tests/Weavie.Hosting.Tests",
        actions: permissionActions,
      }),
    );

    const card = page.locator(".agent-entry-request");
    const buttons = card.locator(".agent-approval-actions button");
    await expect(buttons.filter({ hasText: "Allow once" }).first()).toBeVisible();

    publishPane(paneMessage({ type: "approval-resolved", itemId: "a1", status: "always allowed" }));

    await expect(card.locator(".agent-entry-status")).toHaveText("always allowed");
    await expect(buttons).toHaveCount(0);
  });

  // Regression: a turn boundary must not strip a still-unresolved approval of its hotkeys. The chip and the
  // chord derive from resolution state, not turn state, so the card stays keyboard-answerable while it shows
  // its buttons — even after a turn-completed races in ahead of the answer.
  test("an unresolved approval keeps its hotkeys after the turn reports completed", async ({
    page,
  }) => {
    await mountAgent(page);
    publishCatalog();
    publishPane(paneMessage({ type: "turn-started", turnId: "t1", status: "inProgress" }));
    publishPane(
      paneMessage({
        type: "approval-requested",
        itemId: "a1",
        requestId: "a1",
        status: "pending",
        summary: "Wants to run the test suite.",
        text: "dotnet test tests/Weavie.Hosting.Tests",
        actions: permissionActions,
      }),
    );
    publishPane(paneMessage({ type: "turn-completed", turnId: "t1", status: "completed" }));

    const card = page.locator(".agent-entry-request");
    const accept = card.getByRole("button", { name: /Allow once/ });
    await expect(accept.locator(".agent-key-chip")).toHaveText("Alt+Y");

    await page.locator("[data-agent-composer] textarea").click();
    await page.keyboard.press("Alt+y");
    const decision = await waitForAgentPayload("permission");
    expect(decision).toMatchObject({ requestId: "a1", optionId: "allow-once" });
  });

  // Pins the follow threshold and navigation: staying within three lines keeps following; scrolling farther up pauses it.
  test("scrolling beyond three lines shows jump-to-latest navigation", async ({ page }) => {
    await mountAgent(page);
    for (let i = 0; i < 40; i += 1) {
      publishPane(userMessage(`prompt ${i}\nwith\nseveral\nlines`));
    }

    const body = page.locator(".agent-body");
    const navigation = page.locator(".agent-scroll-nav");
    const latestButton = page.getByRole("button", { name: "Jump to latest", exact: true });
    const distanceFromBottom = (): Promise<number> =>
      body.evaluate((element) => element.scrollHeight - element.scrollTop - element.clientHeight);
    await expect(page.locator(".agent-entry").first()).toBeVisible();
    await expect(latestButton).toHaveCount(0);
    await waitForBottom(page, body);

    const bounds = await body.boundingBox();
    if (bounds === null) {
      throw new Error("agent body has no viewport");
    }
    await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
    const scrollLinesFromBottom = async (lines: number): Promise<void> => {
      const lineHeight = await body.evaluate((element) =>
        Number.parseFloat(getComputedStyle(element).lineHeight),
      );
      await page.mouse.wheel(0, -lineHeight * lines);
      await expect.poll(distanceFromBottom).toBeGreaterThan(lineHeight * lines - 2);
      await expect.poll(distanceFromBottom).toBeLessThan(lineHeight * lines + 2);
    };

    await scrollLinesFromBottom(2.5);
    await expect(latestButton).toHaveCount(0);
    publishPane(userMessage("near-bottom follow check"));
    await waitForBottom(page, body);

    await scrollLinesFromBottom(4);
    await expect(latestButton).toHaveCount(1);
    await expect(navigation).toHaveCSS("opacity", "0");
    await revealScrollNavigation(page);
    await page.screenshot({ path: join(shotsDir, "10-scroll-navigation.png") });
    await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
    await expect(navigation).toHaveCSS("opacity", "0");
    await revealScrollNavigation(page);

    await latestButton.click();
    await expect(latestButton).toHaveCount(0);
    await waitForBottom(page, body);
    publishPane(userMessage("follow after jump to latest"));
    await expect(page.getByText("follow after jump to latest", { exact: true })).toBeVisible();
    await waitForBottom(page, body);
  });

  // Flaked on main CI 2026-08-13 04:09 UTC (e2e (linux) / shard 2/6):
  // https://github.com/Kapps/weavie/actions/runs/31666115997/job/94341238717 — turnButton stuck
  // visible after the jump-to-turn click. Root cause: AgentPaneScroll's agentTurnStartAbove
  // compared the cached measurement start against virtualizer.scrollOffset with strict `<`, and
  // the two can settle a sub-pixel apart (e.g. 745.671875 vs 746) even when aligned, flipping the
  // signal back on with nothing left to correct it. Fixed by adding a 1px tolerance in
  // AgentPaneScroll.ts's updateAgentTurnStartPosition. Reproduced locally at ~5-7% under worker
  // contention before the fix; 80/80 passed after under the same contention.
  test("an overlong turn offers reciprocal turn navigation", async ({ page }) => {
    await mountAgent(page);
    publishCatalog();
    publishPane(
      userMessage(
        Array.from({ length: 40 }, (_, index) => `Earlier line ${index + 1}.`).join("\n"),
      ),
    );
    const turn = { threadId: "thread-long", turnId: "turn-long" };
    publishPane(
      paneMessage({
        ...turn,
        type: "user-message",
        itemId: "prompt-long",
        text: "Explain the long result",
      }),
    );
    publishPane(paneMessage({ ...turn, type: "turn-started", status: "inProgress" }));
    publishPane(
      paneMessage({
        ...turn,
        type: "item-completed",
        itemId: "opening-update",
        itemType: "agentMessage",
        status: "completed",
        text: "Opening update before the final response.",
      }),
    );
    const longResponse = Array.from({ length: 80 }, (_, index) => `Paragraph ${index + 1}.`).join(
      "\n\n",
    );
    publishPane(
      paneMessage({
        ...turn,
        type: "agent-message-delta",
        itemId: "answer-long",
        itemType: "agentMessage",
        text: longResponse,
      }),
    );

    const body = page.locator(".agent-body");
    const navigation = page.locator(".agent-scroll-nav");
    const turnButton = page.getByRole("button", { name: "Jump to turn", exact: true });
    const latestButton = page.getByRole("button", { name: "Jump to latest", exact: true });
    const agentTurnStart = page.locator("[data-agent-turn-output-start]");
    const prompt = page.locator(".agent-entry.agent-tone-user", {
      hasText: "Explain the long result",
    });

    await expect(agentTurnStart).toContainText("Opening update before the final response.");
    await waitForBottom(page, body);
    await expect
      .poll(() =>
        agentTurnStart.evaluate(
          (element) =>
            element.getBoundingClientRect().top -
            (element.closest(".agent-body")?.getBoundingClientRect().top ?? 0),
        ),
      )
      .toBeLessThan(0);
    await expect(turnButton).toHaveCount(0);

    await page.locator("[data-agent-composer] textarea").focus();
    await page.keyboard.press("Alt+ArrowUp");
    await waitForBottom(page, body);
    await expect(latestButton).toHaveCount(0);

    const continuation = "Followed output while the turn remains active.";
    const completedResponse = `${longResponse}\n\n${continuation}`;
    publishPane(
      paneMessage({
        ...turn,
        type: "agent-message-delta",
        itemId: "answer-long",
        itemType: "agentMessage",
        text: `\n\n${continuation}`,
      }),
    );
    await expect(page.getByText(continuation, { exact: true })).toBeVisible();
    await waitForBottom(page, body);
    await page.evaluate(() =>
      document.documentElement.style.setProperty("--terminal-font-size", "20px"),
    );
    await waitForBottom(page, body);

    publishPane(
      paneMessage({
        ...turn,
        type: "item-completed",
        itemId: "answer-long",
        itemType: "agentMessage",
        status: "completed",
        text: completedResponse,
      }),
    );
    await expect(turnButton).toHaveCount(0);
    publishPane(paneMessage({ ...turn, type: "turn-completed", status: "completed" }));

    await expect(turnButton).toHaveCount(1);
    await expect(turnButton).toHaveAttribute(
      "title",
      "Jump to the start of this agent turn (Alt+Up)",
    );
    await waitForBottom(page, body);
    await expect
      .poll(() =>
        body.evaluate((element) => {
          const wrap = element.parentElement;
          const navigation = element.parentElement?.querySelector<HTMLElement>(
            ".agent-scroll-nav-button",
          );
          if (wrap === null || navigation === null) {
            return { bodyFillsWrap: false, clearsScrollbar: false };
          }
          const bodyBounds = element.getBoundingClientRect();
          const wrapBounds = wrap.getBoundingClientRect();
          return {
            bodyFillsWrap:
              Math.abs(bodyBounds.width - wrapBounds.width) < 1 &&
              Math.abs(bodyBounds.right - wrapBounds.right) < 1,
            clearsScrollbar: bodyBounds.right - navigation.getBoundingClientRect().right >= 15.5,
          };
        }),
      )
      .toEqual({ bodyFillsWrap: true, clearsScrollbar: true });
    await expect(navigation).toHaveCSS("opacity", "0");
    await revealScrollNavigation(page);
    await expect
      .poll(() => scrollNavigationIconVerticalOffset(page, "Jump to turn"))
      .toBeLessThan(0.5);

    await turnButton.click();
    await expect
      .poll(() =>
        agentTurnStart.evaluate((element) => {
          const scrollBody = element.closest(".agent-body");
          return Math.abs(
            element.getBoundingClientRect().top -
              (scrollBody?.getBoundingClientRect().top ?? Number.POSITIVE_INFINITY),
          );
        }),
      )
      .toBeLessThan(1);
    await expect
      .poll(() =>
        prompt.evaluate(
          (element) =>
            element.getBoundingClientRect().bottom -
            (element.closest(".agent-body")?.getBoundingClientRect().top ?? 0),
        ),
      )
      .toBeLessThan(0);
    await expect(turnButton).toHaveCount(0);
    await expect(latestButton).toHaveAttribute(
      "title",
      "Scroll to the latest activity and follow it (Alt+Down)",
    );
    const bounds = await body.boundingBox();
    if (bounds === null) {
      throw new Error("agent body has no viewport");
    }
    await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
    await expect(navigation).toHaveCSS("opacity", "0");
    await latestButton.focus();
    await expect(navigation).toHaveCSS("opacity", "1");
    await expect
      .poll(() => scrollNavigationIconVerticalOffset(page, "Jump to latest"))
      .toBeLessThan(0.5);

    await body.evaluate((element) => {
      element.scrollTop += element.clientHeight;
    });
    await expect(turnButton).toHaveCount(1);
    await expect(latestButton).toHaveCount(1);

    await page.keyboard.press("Alt+ArrowDown");
    await waitForBottom(page, body);
    await expect(turnButton).toHaveCount(1);

    await page.keyboard.press("Alt+ArrowUp");
    await expect(latestButton).toHaveCount(1);
    await page.locator("[data-agent-composer] textarea").focus();
    await expect(navigation).toHaveCSS("opacity", "0");
    await body.dispatchEvent("pointerdown", { pointerType: "touch" });
    await expect(navigation).toHaveCSS("opacity", "1");
    await expect(latestButton).toHaveCSS("width", "40px");
    const freshSession = mockSession("cx-scroll-reset", "fresh", "acp");
    host.setSessions([agentSession, freshSession]);
    host.publishSession(freshSession.address, "agent", "controls", controls);
    await page.locator('.session-chip[title^="fresh —"]').click();
    await expect(page.locator(".agent-empty")).toBeVisible();
    await expect(latestButton).toHaveCount(0);
    await expect(turnButton).toHaveCount(0);
    await waitForBottom(page, body);
  });

  test("Up/Down recall previously submitted prompts", async ({ page }) => {
    await mountAgent(page);
    publishPane(userMessage("first prompt"));
    publishPane(userMessage("second prompt"));

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

  test("Up moves through soft-wrapped draft lines before recalling history", async ({ page }) => {
    await mountAgent(page);
    publishPane(userMessage("previous prompt"));

    const textarea = page.locator("[data-agent-composer] textarea");
    const draft = "one two three four five six seven eight";
    await textarea.evaluate((element) => {
      element.style.width = "120px";
    });
    await textarea.fill(draft);
    await textarea.evaluate((element) => {
      element.setSelectionRange(element.value.length, element.value.length);
    });

    let previousCaret = draft.length;
    let movedWithinDraft = false;
    for (;;) {
      await page.keyboard.press("ArrowUp");
      const value = await textarea.inputValue();
      if (value !== draft) {
        expect(value).toBe("previous prompt");
        break;
      }

      const caret = await textarea.evaluate((element) => element.selectionStart);
      expect(caret).toBeLessThan(previousCaret);
      previousCaret = caret;
      movedWithinDraft = true;
    }
    expect(movedWithinDraft).toBe(true);
  });

  test("Down moves through a soft-wrapped recalled prompt before restoring the draft", async ({
    page,
  }) => {
    await mountAgent(page);
    const prompt = "one two three four five six seven eight";
    publishPane(userMessage(prompt));

    const textarea = page.locator("[data-agent-composer] textarea");
    await textarea.evaluate((element) => {
      element.style.width = "120px";
    });
    await textarea.fill("live draft");
    const releaseAnimationFrames = await page.evaluateHandle(() => {
      const requestAnimationFrame = window.requestAnimationFrame;
      const cancelAnimationFrame = window.cancelAnimationFrame;
      const frames = new Map<number, FrameRequestCallback>();
      let nextFrame = 0;
      window.requestAnimationFrame = (callback) => {
        const frame = ++nextFrame;
        frames.set(frame, callback);
        return frame;
      };
      window.cancelAnimationFrame = (frame) => frames.delete(frame);
      return () => {
        window.requestAnimationFrame = requestAnimationFrame;
        window.cancelAnimationFrame = cancelAnimationFrame;
        for (const callback of frames.values()) {
          callback(performance.now());
        }
      };
    });
    await page.keyboard.press("ArrowUp");
    await expect(textarea).toHaveValue(prompt);
    await expect
      .poll(() => textarea.evaluate((element) => element.selectionStart))
      .toBe(prompt.length);

    await page.keyboard.press("ArrowUp");
    await expect(textarea).toHaveValue(prompt);
    let previousCaret = await textarea.evaluate((element) => element.selectionStart);
    expect(previousCaret).toBeLessThan(prompt.length);
    await releaseAnimationFrames.evaluate((release) => release());
    await releaseAnimationFrames.dispose();
    await expect
      .poll(() => textarea.evaluate((element) => element.selectionStart))
      .toBe(previousCaret);

    for (;;) {
      await page.keyboard.press("ArrowDown");
      const value = await textarea.inputValue();
      if (value !== prompt) {
        expect(value).toBe("live draft");
        break;
      }

      const caret = await textarea.evaluate((element) => element.selectionStart);
      expect(caret).toBeGreaterThan(previousCaret);
      previousCaret = caret;
    }
  });
});
