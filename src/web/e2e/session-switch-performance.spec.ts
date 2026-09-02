import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test } from "@playwright/test";
import { PIXEL_RED } from "./harness/git-workspace";
import { measureSessionSwitch, type SessionSwitchExpectation } from "./harness/session-switch";
import { MockHost, type MockSession, mockSession } from "./mock-host";

const distDir = join(dirname(fileURLToPath(import.meta.url)), "..", "dist");
const SWITCH_BUDGET_MS = 1_000;
// 2026-08-13: flaked on main (102.9ms) under real CI's 2-worker contention:
// https://github.com/Kapps/weavie/actions/runs/31705392679/job/94465045518
// Reproduced locally under matching 2-worker contention: single-switch samples ranged
// 33ms-437ms (vs. a single sub-100ms sample in the original budget), confirming this is
// GC/paint jank from concurrent browser instances sharing the runner's cores, not a
// regression in the preprojected-pane switch itself. Widened with real headroom while
// staying an order of magnitude under SWITCH_BUDGET_MS, so a regression to
// virtualized-row-style re-rendering would still fail this test.
const TOOL_HEAVY_SWITCH_BUDGET_MS = 350;
// 2026-08-15: flaked on main (1072.5ms vs. the 1000ms SWITCH_BUDGET_MS) on the macOS CI runner:
// https://github.com/Kapps/weavie/actions/runs/31861262573/job/94955236768
// macOS runners are consistently noisier than Linux for this measured-virtual-window switch;
// the other three samples in the same run were well under budget. Given its own budget with
// headroom rather than widening the shared SWITCH_BUDGET_MS used by the warm-editor-state test.
const LONG_TRANSCRIPT_SWITCH_BUDGET_MS = 1_500;
const CLAUDE_ACTIVE = "/workspace/claude/active.ts";
const CLAUDE_LATE = "/workspace/claude/background.ts";
const CLAUDE_OTHER = "/workspace/claude/other.ts";
const CLAUDE_PREVIEW_A = "/workspace/claude/preview-a.ts";
const CLAUDE_PREVIEW_B = "/workspace/claude/preview-b.ts";
const ACP_OTHER = "/workspace/acp/notes.ts";
const ACP_IMAGE = "/workspace/acp/pixel.png";

interface SessionFixture {
  catalog: MockSession;
  tabs: string[];
  active: string;
  marker: string | null;
}

const claude: SessionFixture = {
  catalog: mockSession("claude-tabs", "claude-tabs", "claude"),
  tabs: [CLAUDE_ACTIVE, CLAUDE_OTHER],
  active: CLAUDE_ACTIVE,
  marker: "CLAUDE_ACTIVE_MARKER",
};
const acp: SessionFixture = {
  catalog: mockSession("acp-image", "acp-image", "acp"),
  tabs: [ACP_OTHER, ACP_IMAGE],
  active: ACP_IMAGE,
  marker: null,
};
const warmClaude: SessionFixture = {
  ...claude,
  tabs: [...claude.tabs, CLAUDE_PREVIEW_B],
  active: CLAUDE_PREVIEW_B,
  marker: "CLAUDE_PREVIEW_B",
};

function restore(host: MockHost, fixture: SessionFixture): void {
  host.publishSession(fixture.catalog.address, "editor", "restore", {
    session: {
      active: fixture.active,
      open: fixture.tabs.map((path) => ({ path, viewState: null })),
    },
  });
}

function expectation(fixture: SessionFixture): SessionSwitchExpectation {
  const activeTab = fixture.active.split("/").at(-1) as string;
  return {
    label: fixture.catalog.label,
    surface: fixture.catalog.providerId === "claude" ? "terminal" : "structured-agent",
    tabs: fixture.tabs.map((path) => path.split("/").at(-1) as string),
    activeTab,
    content:
      fixture.marker === null
        ? {
            kind: "image",
            pathSuffix: `/${activeTab}`,
            sessionId: fixture.catalog.address.incarnation,
          }
        : { kind: "text", pathSuffix: fixture.active, marker: fixture.marker },
  };
}

test.beforeAll(() => {
  if (!existsSync(join(distDir, "index.html"))) {
    throw new Error(
      `built app not found at ${distDir}; run \`pnpm run build\` before the e2e tests`,
    );
  }
});

test("warm session-owned editor state switches fully paint within budget", async ({ page }) => {
  const host = await MockHost.start({
    distDir,
    sessions: [claude.catalog, acp.catalog],
    files: {
      [CLAUDE_ACTIVE]: "export const value = 'CLAUDE_ACTIVE_MARKER';\n",
      [CLAUDE_OTHER]: "export const other = true;\n",
      [CLAUDE_PREVIEW_A]: "export const value = 'CLAUDE_PREVIEW_A';\n",
      [CLAUDE_PREVIEW_B]: "export const value = 'CLAUDE_PREVIEW_B';\n",
      [ACP_OTHER]: "export const note = true;\n",
    },
  });
  host.setMedia(acp.catalog.address.incarnation, ACP_IMAGE, PIXEL_RED);

  try {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    restore(host, claude);
    restore(host, acp);
    await expect(page.locator(".editor")).toHaveAttribute("data-ready", "true", {
      timeout: 60_000,
    });
    await expect(page.locator(".monaco-editor .view-lines").first()).toContainText(claude.marker);

    host.publishSession(claude.catalog.address, "editor", "openFile", {
      path: CLAUDE_PREVIEW_A,
      line: 1,
      preview: true,
    });
    await expect(page.locator(".monaco-editor .view-lines").first()).toContainText(
      "CLAUDE_PREVIEW_A",
    );
    host.publishSession(claude.catalog.address, "editor", "openFile", {
      path: CLAUDE_PREVIEW_B,
      line: 1,
      preview: true,
    });
    await expect(page.locator(".monaco-editor .view-lines").first()).toContainText(
      warmClaude.marker,
    );
    const retained = await page.evaluate(() => [...(window.__WEAVIE_EDITOR_REFS__?.keys() ?? [])]);
    expect(retained.some((key) => key.includes(CLAUDE_PREVIEW_A))).toBe(false);
    expect(retained.some((key) => key.includes(CLAUDE_PREVIEW_B))).toBe(true);

    // A warm switch is entirely client-owned: it must not depend on another host file read to repaint.
    host.pauseFileProvider();

    const claudeToACP: number[] = [];
    const acpToClaude: number[] = [];
    for (let sample = 0; sample < 3; sample++) {
      claudeToACP.push(await measureSessionSwitch(page, expectation(acp)));
      acpToClaude.push(await measureSessionSwitch(page, expectation(warmClaude)));
    }
    const measurements = {
      budgetMs: SWITCH_BUDGET_MS,
      claudeToACP,
      acpToClaude,
    };
    await test.info().attach("session-switch-performance.json", {
      body: Buffer.from(JSON.stringify(measurements, null, 2)),
      contentType: "application/json",
    });

    expect(Math.max(...claudeToACP)).toBeLessThan(SWITCH_BUDGET_MS);
    expect(Math.max(...acpToClaude)).toBeLessThan(SWITCH_BUDGET_MS);

    await page.locator(`.session-chip[title^="${acp.catalog.label} —"]`).click();
    await expect(page.locator(".editor-media img")).toHaveJSProperty("naturalWidth", 8);
    expect(await page.evaluate(() => window.__WEAVIE_EDITOR_REFS__?.size)).toBe(2);
    host.setSessions([acp.catalog]);
    await expect.poll(() => page.evaluate(() => window.__WEAVIE_EDITOR_REFS__?.size)).toBe(0);
  } finally {
    host.resumeFileProvider();
    await host.close();
  }
});

test("discarding a dirty scratch cancels its save before model reconciliation", async ({
  page,
}) => {
  const scratch = "/scratch/Untitled-1";
  const host = await MockHost.start({
    distDir,
    sessions: [claude.catalog],
    files: { [scratch]: "" },
  });

  try {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    host.publishSession(claude.catalog.address, "editor", "restore", {
      session: {
        active: scratch,
        open: [{ path: scratch, viewState: null, scratch: true }],
      },
    });
    await expect(page.locator(".editor")).toHaveAttribute("data-ready", "true", {
      timeout: 60_000,
    });
    await expect(page.locator(".editor")).toHaveAttribute("data-active-file", scratch);

    host.pauseFileProvider();
    const checkpoint = host.checkpoint();
    await page.evaluate(async () => {
      const editor = (
        window as Window & {
          __WEAVIE_EDITOR__?: { getModel(): { setValue(value: string): void } | null };
        }
      ).__WEAVIE_EDITOR__;
      editor?.getModel()?.setValue("SCRATCHEDIT");
      document.querySelector<HTMLButtonElement>(".editor-tab-close")?.click();
      await new Promise<void>((resolve) => {
        const confirm = (): void => {
          const button = document.querySelector<HTMLButtonElement>(".confirm-btn-primary");
          if (button === null) {
            requestAnimationFrame(confirm);
            return;
          }
          button.click();
          resolve();
        };
        confirm();
      });
      await new Promise((resolve) => setTimeout(resolve, 350));
    });

    await expect(page.locator(".editor-tab")).toHaveCount(0);
    const afterClose = host.received.slice(checkpoint);
    expect(
      afterClose.some((message) => message.feature === "files" && message.name === "write"),
    ).toBe(false);
    expect(
      afterClose.some(
        (message) => message.feature === "editor" && message.name === "discardScratch",
      ),
    ).toBe(true);
  } finally {
    host.resumeFileProvider();
    await host.close();
  }
});

test("long transcripts switch as a measured virtual window", async ({ page }) => {
  const first = mockSession("long-first", "long-first", "acp");
  const second = mockSession("long-second", "long-second", "acp");
  const host = await MockHost.start({ distDir, sessions: [first, second] });
  const transcript = (prefix: string) =>
    Array.from({ length: 800 }, (_, index) => ({
      providerId: "acp",
      type: "item-completed",
      itemId: `${prefix}-${index}`,
      itemType: "agentMessage",
      status: "completed",
      text: `### ${prefix}_${index}\n\n${Array.from(
        { length: (index % 5) + 1 },
        (_, paragraph) => `Paragraph ${paragraph + 1} with **formatted** transcript content.`,
      ).join("\n\n")}`,
    }));
  host.setAgentHistory(first.address, {
    generation: 1,
    messages: transcript("FIRST"),
    pageSize: 400,
  });
  host.setAgentHistory(second.address, {
    generation: 1,
    messages: transcript("SECOND"),
    pageSize: 400,
  });

  try {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();

    const rows = page.locator(".agent-virtual-row");
    const body = page.locator(".agent-body");
    await expect(page.getByText("FIRST_799", { exact: true })).toBeVisible({ timeout: 60_000 });
    expect(await rows.count()).toBeLessThan(40);
    await expect
      .poll(() =>
        body.evaluate((element) => element.scrollHeight - element.scrollTop - element.clientHeight),
      )
      .toBeLessThanOrEqual(1);

    const measureSwitch = (label: string, marker: string): Promise<number> =>
      page.evaluate(
        async (target) => {
          const chip = [...document.querySelectorAll<HTMLButtonElement>(".session-chip")].find(
            (candidate) => candidate.title.startsWith(`${target.label} —`),
          );
          if (chip === undefined) {
            throw new Error(`missing session chip ${target.label}`);
          }
          const complete = (): boolean => {
            const active = document.querySelector<HTMLButtonElement>(".session-chip.active");
            const body = document.querySelector<HTMLElement>(".agent-body");
            const rows = [...document.querySelectorAll<HTMLElement>(".agent-virtual-row")];
            return (
              active?.title.startsWith(`${target.label} —`) === true &&
              body !== null &&
              body.scrollHeight - body.scrollTop - body.clientHeight <= 1 &&
              rows.length < 40 &&
              rows.some((row) => row.textContent?.includes(target.marker) === true)
            );
          };
          const nextFrame = (): Promise<void> =>
            new Promise((resolve) => requestAnimationFrame(() => resolve()));
          const started = performance.now();
          chip.click();
          for (;;) {
            await nextFrame();
            if (complete()) {
              await nextFrame();
              if (complete()) {
                return performance.now() - started;
              }
            }
          }
        },
        { label, marker },
      );

    const measurements = [
      await measureSwitch(second.label, "SECOND_799"),
      await measureSwitch(first.label, "FIRST_799"),
      await measureSwitch(second.label, "SECOND_799"),
      await measureSwitch(first.label, "FIRST_799"),
    ];
    await test.info().attach("long-transcript-session-switch.json", {
      body: Buffer.from(
        JSON.stringify({ budgetMs: LONG_TRANSCRIPT_SWITCH_BUDGET_MS, measurements }, null, 2),
      ),
      contentType: "application/json",
    });
    expect(Math.max(...measurements)).toBeLessThan(LONG_TRANSCRIPT_SWITCH_BUDGET_MS);

    await body.evaluate((element) => {
      element.scrollTop = element.scrollHeight * 0.45;
    });
    await expect(page.locator(".agent-scroll-nav-button")).toHaveCount(1);
    const viewportAnchor = () =>
      body.evaluate((element) => {
        const viewportTop = element.getBoundingClientRect().top;
        const row = [...element.querySelectorAll<HTMLElement>(".agent-virtual-row")].find(
          (candidate) => candidate.getBoundingClientRect().bottom > viewportTop,
        );
        if (row?.dataset.transcriptEntry === undefined) {
          throw new Error("virtual viewport has no anchor row");
        }
        return {
          entryId: row.dataset.transcriptEntry,
          height: element.scrollHeight,
          offset: row.getBoundingClientRect().top - viewportTop,
          top: element.scrollTop,
        };
      });
    const settledViewportAnchor = async () => {
      let previous = await viewportAnchor();
      let stableFrames = 0;
      while (stableFrames < 2) {
        await page.evaluate(
          () => new Promise<void>((resolve) => requestAnimationFrame(() => resolve())),
        );
        const current = await viewportAnchor();
        const unchanged =
          current.entryId === previous.entryId &&
          Math.abs(current.height - previous.height) < 1 &&
          Math.abs(current.offset - previous.offset) < 1 &&
          Math.abs(current.top - previous.top) < 1;
        stableFrames = unchanged ? stableFrames + 1 : 0;
        previous = current;
      }
      return previous;
    };
    const saved = await settledViewportAnchor();
    await measureSwitch(second.label, "SECOND_799");
    await page.locator(`.session-chip[title^="${first.label} —"]`).click();
    await expect(page.locator(`.session-chip.active[title^="${first.label} —"]`)).toBeVisible();
    await expect(page.locator(".agent-scroll-nav-button")).toHaveCount(1);
    const restored = await settledViewportAnchor();
    expect(restored.entryId).toBe(saved.entryId);
    expect(Math.abs(restored.offset - saved.offset)).toBeLessThan(1);
    expect(await rows.count()).toBeLessThan(40);
  } finally {
    await host.close();
  }
});

test("tool-heavy transcripts switch through one preprojected structured pane", async ({ page }) => {
  const first = mockSession("tool-heavy-first", "tool-heavy-first", "acp");
  const second = mockSession("tool-heavy-second", "tool-heavy-second", "acp");
  const host = await MockHost.start({ distDir, sessions: [first, second] });
  const transcript = (turnId: string, count: number) => ({
    messages: [
      { providerId: "acp", type: "user-message", turnId, text: `Run ${count} commands` },
      ...Array.from({ length: count }, (_, index) => ({
        providerId: "acp",
        type: "item-completed",
        turnId,
        itemId: `command-${index}`,
        itemType: "commandExecution",
        status: "completed",
        summary: `command ${index}`,
      })),
    ],
  });
  host.setAgentHistory(first.address, {
    generation: 1,
    messages: transcript("first-turn", 10_000).messages,
    pageSize: 1_000,
  });
  host.setAgentHistory(second.address, {
    generation: 1,
    messages: transcript("second-turn", 15_000).messages,
    pageSize: 1_000,
  });
  try {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();

    const surface = page.locator(".agent-surface");
    await expect(surface).toHaveCount(1);
    await expect(surface).toContainText("ran 10000 commands");
    await expect(page.getByText("history 10000", { exact: true })).toBeVisible();
    expect(await page.locator(".toast-msg").allTextContents()).toEqual([]);
    await expect
      .poll(() => {
        const counts: Record<string, number> = {};
        for (const message of host.received) {
          if (
            message.kind === "request" &&
            message.scope === "session" &&
            message.session !== null &&
            message.feature === "agent" &&
            message.name === "historyPage"
          ) {
            counts[message.session.slot] = (counts[message.session.slot] ?? 0) + 1;
          }
        }
        return counts;
      })
      .toEqual({
        [first.address.slot]: 11,
        [second.address.slot]: 16,
      });
    await page.evaluate(
      () =>
        new Promise<void>((resolve) =>
          requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
        ),
    );
    const outgoing = await surface.elementHandle();
    if (outgoing === null) {
      throw new Error("missing outgoing structured surface");
    }

    const switchMs = await page.evaluate(
      async ({ label, expectedSummary }) => {
        const chip = [...document.querySelectorAll<HTMLButtonElement>(".session-chip")].find(
          (candidate) => candidate.title.startsWith(`${label} —`),
        );
        if (chip === undefined) {
          throw new Error(`missing session chip ${label}`);
        }
        const nextFrame = (): Promise<void> =>
          new Promise((resolve) => requestAnimationFrame(() => resolve()));
        const started = performance.now();
        chip.click();
        for (;;) {
          await nextFrame();
          const active = document.querySelector<HTMLButtonElement>(".session-chip.active");
          const surfaces = document.querySelectorAll(".agent-surface");
          if (
            active?.title.startsWith(`${label} —`) === true &&
            surfaces.length === 1 &&
            surfaces[0]?.textContent?.includes(expectedSummary) === true
          ) {
            await nextFrame();
            return performance.now() - started;
          }
        }
      },
      { label: second.label, expectedSummary: "ran 15000 commands" },
    );
    await test.info().attach("tool-heavy-session-switch.json", {
      body: Buffer.from(
        JSON.stringify(
          { activitySteps: 15_000, budgetMs: TOOL_HEAVY_SWITCH_BUDGET_MS, switchMs },
          null,
          2,
        ),
      ),
      contentType: "application/json",
    });
    expect(switchMs).toBeLessThan(TOOL_HEAVY_SWITCH_BUDGET_MS);
    await expect(surface).toHaveCount(1);
    await expect(surface).toContainText("ran 15000 commands");
    await expect(page.getByText("history 15000", { exact: true })).toBeVisible();
    await expect(page.locator(".agent-activity-list")).toHaveCount(0);
    expect(await outgoing.evaluate((element) => element.isConnected)).toBe(false);

    host.setSessions([first]);
    await expect(surface).toContainText("ran 10000 commands");
    await expect(surface).not.toContainText("ran 15000 commands");

    const replacement = {
      ...mockSession(second.id, "replacement", "acp"),
      address: { slot: second.address.slot, incarnation: "tool-heavy-replacement" },
    };
    host.setSessions([first, replacement]);
    await page.getByTitle(new RegExp(`^${replacement.label} —`)).click();
    await expect(surface).toHaveCount(1);
    await expect(surface).not.toContainText("ran 15000 commands");
  } finally {
    await host.close();
  }
});

test("remounting a structured pane preserves the session-owned edited draft", async ({ page }) => {
  const first = mockSession("draft-first", "draft-first", "acp");
  const second = mockSession("draft-second", "draft-second", "acp");
  const host = await MockHost.start({ distDir, sessions: [first, second] });
  const messages = [
    { providerId: "acp" as const, type: "draft", text: "provider prefill" },
    {
      providerId: "acp" as const,
      type: "user-message",
      turnId: "draft-turn",
      text: "work",
    },
    ...["one", "two"].map((itemId) => ({
      providerId: "acp" as const,
      type: "item-completed",
      turnId: "draft-turn",
      itemId,
      itemType: "commandExecution",
      status: "completed",
      summary: itemId,
    })),
  ];
  host.setAgentHistory(first.address, {
    generation: 1,
    messages,
    pageSize: 100,
  });

  try {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();

    const textarea = page.locator("[data-agent-composer] textarea");
    await expect(textarea).toHaveValue("provider prefill");
    await textarea.fill("user-edited draft");
    await page.getByText("history 2", { exact: true }).click();
    await expect(page.locator(".agent-activity-list .agent-activity-step")).toHaveCount(2);
    await page.getByTitle(new RegExp(`^${second.label} —`)).click();
    await expect(textarea).toHaveValue("");
    await page.getByTitle(new RegExp(`^${first.label} —`)).click();
    await expect(textarea).toHaveValue("user-edited draft");
    await expect(page.locator(".agent-activity-details")).toHaveAttribute("open", "");
    await expect(page.locator(".agent-activity-list .agent-activity-step")).toHaveCount(2);
    const reconnectCheckpoint = host.checkpoint();
    host.disconnectBridge();
    await host.waitUntilConnected(reconnectCheckpoint);
    await expect(textarea).toHaveValue("user-edited draft");
    host.publishAgentPane(first.address, {
      providerId: "acp",
      type: "draft",
      text: "new provider prefill",
    });
    await expect(textarea).toHaveValue("new provider prefill");
    host.setAgentHistory(first.address, {
      generation: 2,
      messages: messages.slice(1),
      pageSize: 100,
    });
    host.publishSession(first.address, "agent", "paneReset", {});
    await expect(page.locator(".agent-activity-details")).not.toHaveAttribute("open", "");
    await expect(page.locator(".agent-activity-list")).toHaveCount(0);
    await expect(page.locator(".agent-surface")).toHaveCount(1);
  } finally {
    await host.close();
  }
});

test("a message for a background session mutates only its owned editor state", async ({ page }) => {
  const host = await MockHost.start({
    distDir,
    sessions: [claude.catalog, acp.catalog],
    files: {
      [CLAUDE_ACTIVE]: "export const value = 'CLAUDE_ACTIVE_MARKER';\n",
      [CLAUDE_LATE]: "export const value = 'BACKGROUND_SESSION_MARKER';\n",
      [ACP_OTHER]: "export const note = true;\n",
    },
  });
  host.setMedia(acp.catalog.address.incarnation, ACP_IMAGE, PIXEL_RED);

  try {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    restore(host, claude);
    restore(host, acp);
    await page.locator(`.session-chip[title^="${acp.catalog.label} —"]`).click();
    await expect(page.locator(".editor-media img")).toHaveJSProperty("naturalWidth", 8);

    host.publishSession(claude.catalog.address, "editor", "openFile", {
      path: CLAUDE_LATE,
      line: 1,
      preview: false,
    });
    await expect(page.locator(".editor-media img")).toHaveJSProperty("naturalWidth", 8);
    await expect(page.locator(".editor-tab", { hasText: "background.ts" })).toHaveCount(0);

    const checkpoint = host.checkpoint();
    await page.locator(`.session-chip[title^="${claude.catalog.label} —"]`).click();
    await host.waitForSession(claude.catalog.address, "request", "files", "stat", checkpoint);
    await expect(page.locator(".editor-tab", { hasText: "background.ts" })).toBeVisible();
    await expect(page.locator(".monaco-editor .view-lines").first()).toContainText(
      "BACKGROUND_SESSION_MARKER",
    );
  } finally {
    await host.close();
  }
});

test("a delayed background file response cannot repaint the selected session", async ({ page }) => {
  const host = await MockHost.start({
    distDir,
    sessions: [claude.catalog, acp.catalog],
    files: {
      [CLAUDE_LATE]: "export const value = 'BACKGROUND_SESSION_MARKER';\n",
      [ACP_OTHER]: "export const note = true;\n",
    },
  });
  host.setMedia(acp.catalog.address.incarnation, ACP_IMAGE, PIXEL_RED);

  try {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    host.publishSession(claude.catalog.address, "editor", "restore", {
      session: { active: null, open: [] },
    });
    restore(host, acp);
    await expect(page.locator(".editor")).toHaveAttribute("data-ready", "true", {
      timeout: 60_000,
    });

    host.pauseFileProvider();
    const checkpoint = host.checkpoint();
    host.publishSession(claude.catalog.address, "editor", "openFile", {
      path: CLAUDE_LATE,
      line: 1,
      preview: false,
    });
    await host.waitForSession(claude.catalog.address, "request", "files", "stat", checkpoint);

    await page.locator(`.session-chip[title^="${acp.catalog.label} —"]`).click();
    await expect(page.locator(".editor-media img")).toHaveJSProperty("naturalWidth", 8);
    host.resumeFileProvider();
    await expect(page.locator(".editor-media img")).toHaveAttribute(
      "src",
      new RegExp(
        `session=${acp.catalog.address.incarnation}.*path=${encodeURIComponent(ACP_IMAGE)}`,
      ),
    );
    await expect(page.locator(".editor-tab.active .editor-tab-label")).toHaveText("pixel.png");
  } finally {
    host.resumeFileProvider();
    await host.close();
  }
});
