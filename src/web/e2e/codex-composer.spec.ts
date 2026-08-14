import { existsSync, mkdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, type Page, test } from "@playwright/test";
import type { CommandInfo } from "../src/commands/types";
import { MockHost, mockSession } from "./mock-host";

// Drives the native Codex composer in a real browser against the mock host: it renders the structured agent
// pane from a pushed session-list, feeds it provider-neutral `agent-controls`, and exercises the three new
// features end to end — status line + live picker, `/` slash menu, and Up/Down prompt history — asserting both
// the rendered UI and the session-owned `agent.setControl` event the picker sends back. Screenshots land in
// .recordings for review.

const here = dirname(fileURLToPath(import.meta.url));
const distDir = join(here, "..", "dist");
const shotsDir = join(here, ".recordings", "codex-composer");

const codexSession = mockSession("cx", "codex", "codex");

const controls = {
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
  providerId: "codex",
  ...message,
});

const userMessage = (text: string) => paneMessage({ type: "user-message", text });

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
const turnNavigationWhen = "agentFocused && agentTurnNavigable";
const catalog = {
  commands: [
    agentCommand("weavie.agent.submit", "Submit Agent Prompt", "agentComposerFocused", ["enter"]),
    agentCommand("weavie.agent.interrupt", "Interrupt Agent Turn", "agentFocused", ["escape"]),
    agentCommand("weavie.agent.jumpToTurn", "Jump to Agent Turn", turnNavigationWhen, ["alt+up"]),
    agentCommand("weavie.agent.jumpToLatest", "Jump to Latest Agent Activity", "agentFocused", [
      "alt+down",
    ]),
    agentCommand("weavie.agent.openPlan", "Open Agent Plan", "agentFocused", ["alt+p"]),
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
    agentCommand("weavie.diff.againstHead", "Diff Against HEAD", "", []),
    agentCommand("weavie.review.open", "Review Changes", "", []),
  ],
  keybindings: [
    { key: "enter", command: "weavie.agent.submit", when: "agentComposerFocused" },
    { key: "escape", command: "weavie.agent.interrupt", when: "agentFocused" },
    { key: "alt+up", command: "weavie.agent.jumpToTurn", when: turnNavigationWhen },
    { key: "alt+down", command: "weavie.agent.jumpToLatest", when: "agentFocused" },
    { key: "alt+p", command: "weavie.agent.openPlan", when: "agentFocused" },
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
    host = await MockHost.start({ distDir, sessions: [codexSession] });
  });

  test.afterEach(async () => {
    await host.close();
  });

  const publishPane = (message: Record<string, unknown>): void =>
    host.publishAgentPane(codexSession.address, message);

  const publishControls = (value: typeof controls): void =>
    host.publishSession(codexSession.address, "agent", "controls", value);

  const publishCatalog = (): void => host.publishHost("commands", "catalog", catalog);

  const waitForAgentEvent = (name: string) =>
    host.waitForSession(codexSession.address, "event", "agent", name);

  const waitForAgentPayload = async (name: string): Promise<Record<string, unknown>> =>
    (await waitForAgentEvent(name)).payload as Record<string, unknown>;

  const lastAgentPayload = (name: string): Record<string, unknown> | undefined =>
    host.received
      .filter(
        (message) =>
          message.scope === "session" &&
          message.session?.incarnation === codexSession.address.incarnation &&
          message.kind === "event" &&
          message.feature === "agent" &&
          message.name === name,
      )
      .at(-1)?.payload as Record<string, unknown> | undefined;

  // Mounts the Codex session and its control surface after the exact-session hello creates its owner.
  async function mountCodex(page: Page): Promise<void> {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    const statusLine = page.locator(".agent-status-line");
    publishControls(controls);
    await expect(statusLine).toBeVisible();
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
    const textareaLeft = await page
      .locator("[data-agent-composer] textarea")
      .evaluate((element) => element.getBoundingClientRect().left);
    const modelLabelLeft = await page
      .locator(".agent-status-model .agent-status-value")
      .evaluate((element) => element.getBoundingClientRect().left);
    expect(textareaLeft).toBe(modelLabelLeft);
    await page.screenshot({ path: join(shotsDir, "01-status-line.png") });
    await page.locator(".agent-compose").screenshot({ path: join(shotsDir, "00-compose-row.png") });
  });

  test("agent prose, code, composer, and chrome use the shared typography roles", async ({
    page,
  }) => {
    await mountCodex(page);
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

  test("mouse clicks return to the prompt without taking text selection or response-field focus", async ({
    page,
  }) => {
    await mountCodex(page);

    const textarea = page.locator("[data-agent-composer] textarea");
    const run = page.locator("[data-agent-composer] button[type='submit']");
    await expect(run).toBeDisabled();
    await page.locator(".agent-status-model").focus();
    await run.click({ force: true });
    await expect(textarea).toBeFocused();

    await textarea.fill("ready");
    await page.locator(".agent-surface .pane-head").click();
    await expect(textarea).toBeFocused();
    await page.keyboard.type(" after chrome");

    await page.locator(".agent-status-model").click();
    await expect(page.locator(".agent-model-picker")).toBeVisible();
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
        status: "pending",
        questions: [
          {
            id: "answer",
            header: "Answer",
            question: "What should Codex do?",
            isSecret: false,
            options: [],
          },
        ],
      }),
    );
    const response = page.locator(".agent-input-request input");
    await response.click();
    await expect(response).toBeFocused();
    await response.fill("Keep this field focused");
    await expect(response).toHaveValue("Keep this field focused");
  });

  test("an input request stays docked while later updates scroll beneath it", async ({ page }) => {
    await mountCodex(page);
    await page.setViewportSize({ width: 800, height: 500 });
    publishPane(
      paneMessage({
        type: "input-requested",
        itemId: "input-draft",
        status: "pending",
        questions: [
          {
            id: "answer",
            header: "Answer",
            question: "What should Codex do?",
            isSecret: false,
            options: [],
          },
        ],
      }),
    );
    const dock = page.locator("[data-agent-pending-request]");
    const response = dock.locator(".agent-input-request input");
    const body = page.locator(".agent-body");
    const distanceFromBottom = (): Promise<number> =>
      body.evaluate((element) => element.scrollHeight - element.scrollTop - element.clientHeight);
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
    await expect.poll(distanceFromBottom).toBeLessThan(1);

    publishPane(paneMessage({ type: "input-resolved", itemId: "input-draft", status: "resolved" }));
    await expect(dock).toHaveCount(0);
    await expect.poll(distanceFromBottom).toBeLessThan(1);
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

  test("status line shows Git's HEAD diff instead of the review aggregation", async ({ page }) => {
    await mountCodex(page);
    publishCatalog();
    host.files.set("/workspace/one.ts", "export const one = true;\n");
    host.publishSession(codexSession.address, "review", "changes", {
      label: "",
      files: [
        { path: "/workspace/one.ts", name: "one.ts", added: 7, removed: 1, line: 2 },
        { path: "/workspace/two.ts", name: "two.ts", added: 5, removed: 3, line: 4 },
      ],
    });
    host.publishSession(codexSession.address, "git", "status", {
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
      codexSession.address,
      "event",
      "review",
      "diffAgainst",
    );
    expect(diffAgainst.payload).toMatchObject({ reference: "HEAD" });

    host.publishSession(codexSession.address, "review", "changes", { label: "", files: [] });
    await expect(counts).toHaveText("+3/-8");
    host.publishSession(codexSession.address, "git", "status", {
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
    await mountCodex(page);
    publishCatalog();

    const mode = page.locator(".agent-status-segment", { hasText: "Default" });
    await expect(mode).toHaveAttribute("title", /Shift\+Tab/);
    const textarea = page.locator("[data-agent-composer] textarea");
    await textarea.click();
    await page.keyboard.press("Shift+Tab");
    expect(await waitForAgentPayload("setControl")).toMatchObject({
      axis: "collaborationMode",
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
    await mountCodex(page);
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
        codexSession.address,
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
      host.publishSession(codexSession.address, "editor", "agentPlan", {
        id: "cx:thread-plan:turn-plan:plan-1",
        path: planPath,
        title: "Implementation plan",
        markdown:
          "# Implementation\n\n1. Add the plan document.\n\n" +
          "```mermaid\nflowchart LR\n  A[Plan] --> B[Ship]\n```",
      });
    };
    const openPlan = (): void => {
      host.publishSession(codexSession.address, "editor", "openOverlay", {
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

  test("Alt+P explains when no completed plan is available", async ({ page }) => {
    await mountCodex(page);
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
    await mountCodex(page);
    const url = `${host.url}/pull/123`;
    publishCatalog();
    host.publishSession(codexSession.address, "git", "status", {
      branch: "feat/native-ui-pr",
      dirty: false,
    });
    host.publishSession(codexSession.address, "git", "pullRequest", {
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

    host.publishSession(codexSession.address, "git", "pullRequest", {
      branch: "feat/native-ui-pr",
      pullRequest: { number: 123, url, state: "merged" },
      error: "temporary network failure",
    });
    await expect(link).toHaveText("#123 · Merged");
    await expect(link).toHaveAttribute("title", /last refresh failed: temporary network failure/);
    await expect(page.locator(".agent-status-unavailable")).toHaveCount(0);

    host.publishSession(codexSession.address, "git", "status", {
      branch: "another-branch",
      dirty: false,
    });
    await expect(link).toHaveCount(0);
  });

  test("subagent completion keeps the primary turn working until its own result", async ({
    page,
  }) => {
    await mountCodex(page);
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

    const set = await waitForAgentPayload("setControl");
    expect(set).toMatchObject({ axis: "model", value: "gpt-5.4-mini" });
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

    const set = await waitForAgentPayload("setControl");
    expect(set).toMatchObject({ axis: "effort", value: "high" });
    await expect(page.locator(".agent-model-picker")).toBeHidden();
  });

  test("toggling Fast in the submenu switches the tier and shows the bolt", async ({ page }) => {
    await mountCodex(page);

    await page.locator(".agent-status-model").click();
    const fastItem = page.locator(".agent-model-fast-item");
    await expect(fastItem).toBeVisible();
    await expect(fastItem).not.toHaveClass(/on/);
    await fastItem.click();

    const set = await waitForAgentPayload("setControl");
    expect(set).toMatchObject({ axis: "serviceTier", value: "priority" });

    // The host echoes Fast on; the submenu item reads on and the status-line label gains the bolt.
    publishControls(fastOnControls);
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
    publishControls(controls);
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
    publishControls(controls);
    await expect(options.nth(1)).toHaveClass(/active/);

    // Keyboard selection still applies the highlighted option after the re-push.
    await page.keyboard.press("Enter");
    const set = await waitForAgentPayload("setControl");
    expect(set).toMatchObject({ axis: "approvalPolicy", value: "never" });
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
        status: "pending",
        summary: "Run: dotnet test",
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
    await mountCodex(page);
    const secondSession = mockSession("cx2", "other", "codex");
    host.setSessions([codexSession, secondSession]);
    await expect(page.locator(".session-chip")).toHaveCount(2);
    host.publishSession(secondSession.address, "agent", "controls", controls);
    const working = page.locator(".agent-working");
    const timeText = working.locator(".agent-working-time");
    const readSeconds = async (): Promise<number> => {
      const text = (await timeText.textContent()) ?? "";
      const match = text.match(/(?:(\d+)m\s*)?(\d+)s/);
      return match === null ? -1 : (match[1] ? Number(match[1]) * 60 : 0) + Number(match[2]);
    };

    // Start a turn on the Codex session; let its timer tick past a couple of seconds so a reset would be stark.
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

    // Switch to a different session (no active turn) — the Codex working row leaves with it.
    await page.locator('.session-chip[title^="other —"]').click();
    await expect(working).toHaveCount(0);

    // Sit on the other session for several wall-clock seconds, then return to the still-running Codex turn.
    await page.waitForTimeout(4_000);
    await page.locator('.session-chip[title^="codex —"]').click();
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
    publishCatalog();

    const empty = page.locator(".agent-empty");
    await expect(empty).toBeVisible();
    await expect(empty.locator(".agent-empty-title")).toHaveText("Codex");
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
    await mountCodex(page);
    publishCatalog();
    publishPane(paneMessage({ type: "turn-started", turnId: "t1", status: "inProgress" }));
    publishPane(
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
    const decision = await waitForAgentPayload("approval");
    expect(decision).toMatchObject({ requestId: "a1", decision: "accept" });
  });

  // Regression: once the approval resolves, its decision buttons must go — the card is no longer actionable.
  // The header status flips reactively; the buttons must flip with it in the same live update (no re-mount).
  test("a resolved approval drops its decision buttons in place", async ({ page }) => {
    await mountCodex(page);
    publishCatalog();
    publishPane(paneMessage({ type: "turn-started", turnId: "t1", status: "inProgress" }));
    publishPane(
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

    publishPane(
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
    publishCatalog();
    publishPane(paneMessage({ type: "turn-started", turnId: "t1", status: "inProgress" }));
    publishPane(
      paneMessage({
        type: "approval-requested",
        itemId: "a1",
        status: "pending",
        summary: "Wants to run the test suite.",
        text: "dotnet test tests/Weavie.Hosting.Tests",
      }),
    );
    publishPane(paneMessage({ type: "turn-completed", turnId: "t1", status: "completed" }));

    const card = page.locator(".agent-entry-request");
    const accept = card.locator("button", { hasText: "Accept" }).first();
    await expect(accept.locator(".agent-key-chip")).toHaveText("Alt+Y");

    await page.locator("[data-agent-composer] textarea").click();
    await page.keyboard.press("Alt+y");
    const decision = await waitForAgentPayload("approval");
    expect(decision).toMatchObject({ requestId: "a1", decision: "accept" });
  });

  // Pins the follow threshold and navigation: staying within three lines keeps following; scrolling farther up pauses it.
  test("scrolling beyond three lines shows jump-to-latest navigation", async ({ page }) => {
    await mountCodex(page);
    for (let i = 0; i < 40; i += 1) {
      publishPane(userMessage(`prompt ${i}\nwith\nseveral\nlines`));
    }

    const body = page.locator(".agent-body");
    const navigation = page.locator(".agent-scroll-nav");
    const latestButton = page.getByRole("button", { name: "Jump to latest", exact: true });
    await expect(page.locator(".agent-entry").first()).toBeVisible();
    await expect(latestButton).toHaveCount(0);

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
      await expect
        .poll(() =>
          body.evaluate(
            (element) => element.scrollHeight - element.scrollTop - element.clientHeight,
          ),
        )
        .toBeGreaterThan(lineHeight * lines - 2);
    };

    await scrollLinesFromBottom(2.5);
    await expect(latestButton).toHaveCount(0);
    publishPane(userMessage("near-bottom follow check"));
    await expect
      .poll(() => body.evaluate((el) => el.scrollHeight - el.scrollTop - el.clientHeight))
      .toBeLessThan(1);

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
    await expect
      .poll(() => body.evaluate((el) => el.scrollHeight - el.scrollTop - el.clientHeight))
      .toBeLessThan(1);
    publishPane(userMessage("follow after jump to latest"));
    await expect(page.getByText("follow after jump to latest", { exact: true })).toBeVisible();
    await expect
      .poll(() => body.evaluate((el) => el.scrollHeight - el.scrollTop - el.clientHeight))
      .toBeLessThan(1);
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
    await mountCodex(page);
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
    const distanceFromBottom = (): Promise<number> =>
      body.evaluate((element) => element.scrollHeight - element.scrollTop - element.clientHeight);

    await expect(agentTurnStart).toContainText("Opening update before the final response.");
    await expect.poll(distanceFromBottom).toBeLessThan(1);
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
    await expect.poll(distanceFromBottom).toBeLessThan(1);
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
    await expect.poll(distanceFromBottom).toBeLessThan(1);
    await page.evaluate(() =>
      document.documentElement.style.setProperty("--terminal-font-size", "20px"),
    );
    await expect.poll(distanceFromBottom).toBeLessThan(1);

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
    await expect.poll(distanceFromBottom).toBeLessThan(1);
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
    await expect.poll(distanceFromBottom).toBeLessThan(1);
    await expect(turnButton).toHaveCount(1);

    await page.keyboard.press("Alt+ArrowUp");
    await expect(latestButton).toHaveCount(1);
    await page.locator("[data-agent-composer] textarea").focus();
    await expect(navigation).toHaveCSS("opacity", "0");
    await body.dispatchEvent("pointerdown", { pointerType: "touch" });
    await expect(navigation).toHaveCSS("opacity", "1");
    await expect(latestButton).toHaveCSS("width", "40px");
    const freshSession = mockSession("cx-scroll-reset", "fresh", "codex");
    host.setSessions([codexSession, freshSession]);
    host.publishSession(freshSession.address, "agent", "controls", controls);
    await page.locator('.session-chip[title^="fresh —"]').click();
    await expect(page.locator(".agent-empty")).toBeVisible();
    await expect(latestButton).toHaveCount(0);
    await expect(turnButton).toHaveCount(0);
    await expect.poll(distanceFromBottom).toBeLessThan(1);
  });

  test("Up/Down recall previously submitted prompts", async ({ page }) => {
    await mountCodex(page);
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
    await mountCodex(page);
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
    await mountCodex(page);
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
