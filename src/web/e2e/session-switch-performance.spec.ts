import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test } from "@playwright/test";
import { PIXEL_RED } from "./harness/git-workspace";
import { measureSessionSwitch, type SessionSwitchExpectation } from "./harness/session-switch";
import { MockHost, type MockSession, mockSession } from "./mock-host";

const distDir = join(dirname(fileURLToPath(import.meta.url)), "..", "dist");
const SWITCH_BUDGET_MS = 1_000;
const CLAUDE_ACTIVE = "/workspace/claude/active.ts";
const CLAUDE_LATE = "/workspace/claude/background.ts";
const CLAUDE_OTHER = "/workspace/claude/other.ts";
const CODEX_OTHER = "/workspace/codex/notes.ts";
const CODEX_IMAGE = "/workspace/codex/pixel.png";

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
const codex: SessionFixture = {
  catalog: mockSession("codex-image", "codex-image", "codex"),
  tabs: [CODEX_OTHER, CODEX_IMAGE],
  active: CODEX_IMAGE,
  marker: null,
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
    provider: fixture.catalog.providerId,
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

test("warm session-owned editor state switches fully paint within one second", async ({ page }) => {
  const host = await MockHost.start({
    distDir,
    sessions: [claude.catalog, codex.catalog],
    files: {
      [CLAUDE_ACTIVE]: "export const value = 'CLAUDE_ACTIVE_MARKER';\n",
      [CLAUDE_OTHER]: "export const other = true;\n",
      [CODEX_OTHER]: "export const note = true;\n",
    },
  });
  host.setMedia(codex.catalog.address.incarnation, CODEX_IMAGE, PIXEL_RED);

  try {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    restore(host, claude);
    restore(host, codex);
    await expect(page.locator(".editor")).toHaveAttribute("data-ready", "true", {
      timeout: 60_000,
    });
    await expect(page.locator(".monaco-editor .view-lines").first()).toContainText(claude.marker);

    const claudeToCodex: number[] = [];
    const codexToClaude: number[] = [];
    for (let sample = 0; sample < 3; sample++) {
      claudeToCodex.push(await measureSessionSwitch(page, expectation(codex)));
      codexToClaude.push(await measureSessionSwitch(page, expectation(claude)));
    }
    const measurements = { budgetMs: SWITCH_BUDGET_MS, claudeToCodex, codexToClaude };
    await test.info().attach("session-switch-performance.json", {
      body: Buffer.from(JSON.stringify(measurements, null, 2)),
      contentType: "application/json",
    });

    expect(Math.max(...claudeToCodex)).toBeLessThan(SWITCH_BUDGET_MS);
    expect(Math.max(...codexToClaude)).toBeLessThan(SWITCH_BUDGET_MS);
  } finally {
    await host.close();
  }
});

test("long transcripts switch as a measured virtual window", async ({ page }) => {
  const first = mockSession("long-first", "long-first", "codex");
  const second = mockSession("long-second", "long-second", "codex");
  const host = await MockHost.start({ distDir, sessions: [first, second] });
  const transcript = (prefix: string) =>
    Array.from({ length: 800 }, (_, index) => ({
      providerId: "codex",
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
      body: Buffer.from(JSON.stringify({ budgetMs: SWITCH_BUDGET_MS, measurements }, null, 2)),
      contentType: "application/json",
    });
    expect(Math.max(...measurements)).toBeLessThan(SWITCH_BUDGET_MS);

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

test("a message for a background session mutates only its owned editor state", async ({ page }) => {
  const host = await MockHost.start({
    distDir,
    sessions: [claude.catalog, codex.catalog],
    files: {
      [CLAUDE_ACTIVE]: "export const value = 'CLAUDE_ACTIVE_MARKER';\n",
      [CLAUDE_LATE]: "export const value = 'BACKGROUND_SESSION_MARKER';\n",
      [CODEX_OTHER]: "export const note = true;\n",
    },
  });
  host.setMedia(codex.catalog.address.incarnation, CODEX_IMAGE, PIXEL_RED);

  try {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    restore(host, claude);
    restore(host, codex);
    await page.locator(`.session-chip[title^="${codex.catalog.label} —"]`).click();
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
    sessions: [claude.catalog, codex.catalog],
    files: {
      [CLAUDE_LATE]: "export const value = 'BACKGROUND_SESSION_MARKER';\n",
      [CODEX_OTHER]: "export const note = true;\n",
    },
  });
  host.setMedia(codex.catalog.address.incarnation, CODEX_IMAGE, PIXEL_RED);

  try {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    host.publishSession(claude.catalog.address, "editor", "restore", {
      session: { active: null, open: [] },
    });
    restore(host, codex);
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

    await page.locator(`.session-chip[title^="${codex.catalog.label} —"]`).click();
    await expect(page.locator(".editor-media img")).toHaveJSProperty("naturalWidth", 8);
    host.resumeFileProvider();
    await expect(page.locator(".editor-media img")).toHaveAttribute(
      "src",
      new RegExp(
        `session=${codex.catalog.address.incarnation}.*path=${encodeURIComponent(CODEX_IMAGE)}`,
      ),
    );
    await expect(page.locator(".editor-tab.active .editor-tab-label")).toHaveText("pixel.png");
  } finally {
    host.resumeFileProvider();
    await host.close();
  }
});
