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
  catalog: mockSession("claude-tabs", "claude-tabs", "claude", true),
  tabs: [CLAUDE_ACTIVE, CLAUDE_OTHER],
  active: CLAUDE_ACTIVE,
  marker: "CLAUDE_ACTIVE_MARKER",
};
const codex: SessionFixture = {
  catalog: mockSession("codex-image", "codex-image", "codex", false),
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
