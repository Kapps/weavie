import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test } from "@playwright/test";
import { PIXEL_RED } from "./harness/git-workspace";
import { measureSessionSwitch, type SessionSwitchExpectation } from "./harness/session-switch";
import { MockHost, mockSessionChip } from "./mock-host";

const distDir = join(dirname(fileURLToPath(import.meta.url)), "..", "dist");
const SWITCH_BUDGET_MS = 1_000;
const CLAUDE_ID = "claude-tabs";
const CODEX_ID = "codex-image";
const CLAUDE_ACTIVE = "/workspace/claude/active.ts";
const CLAUDE_OTHER = "/workspace/claude/other.ts";
const CODEX_OTHER = "/workspace/codex/notes.ts";
const CODEX_IMAGE = "/workspace/codex/pixel.png";

interface Projection {
  id: string;
  label: string;
  provider: "claude" | "codex";
  tabs: string[];
  active: string;
  marker: string | null;
}

const claude: Projection = {
  id: CLAUDE_ID,
  label: "claude-tabs",
  provider: "claude",
  tabs: [CLAUDE_ACTIVE, CLAUDE_OTHER],
  active: CLAUDE_ACTIVE,
  marker: "CLAUDE_ACTIVE_MARKER",
};
const codex: Projection = {
  id: CODEX_ID,
  label: "codex-image",
  provider: "codex",
  tabs: [CODEX_OTHER, CODEX_IMAGE],
  active: CODEX_IMAGE,
  marker: null,
};

function sessions(active: string) {
  return [
    mockSessionChip(CLAUDE_ID, claude.label, "claude", active === CLAUDE_ID, true),
    mockSessionChip(CODEX_ID, codex.label, "codex", active === CODEX_ID, false),
  ];
}

function editorSession(projection: Projection) {
  return {
    type: "set-editor-session",
    sessionId: projection.id,
    session: {
      active: projection.active,
      open: projection.tabs.map((path) => ({ path, viewState: null })),
    },
  };
}

function expectation(projection: Projection): SessionSwitchExpectation {
  const activeTab = projection.active.split("/").at(-1) as string;
  return {
    label: projection.label,
    provider: projection.provider,
    tabs: projection.tabs.map((path) => path.split("/").at(-1) as string),
    activeTab,
    content:
      projection.marker === null
        ? { kind: "image", pathSuffix: `/${activeTab}`, sessionId: projection.id }
        : { kind: "text", pathSuffix: projection.active, marker: projection.marker },
  };
}

test.beforeAll(() => {
  if (!existsSync(join(distDir, "index.html"))) {
    throw new Error(
      `built app not found at ${distDir}; run \`pnpm run build\` before the e2e tests`,
    );
  }
});

test("warm Claude/Codex session switches fully paint within one second", async ({ page }) => {
  const host = await MockHost.start({
    distDir,
    files: {
      [CLAUDE_ACTIVE]: "export const value = 'CLAUDE_ACTIVE_MARKER';\n",
      [CLAUDE_OTHER]: "export const other = true;\n",
      [CODEX_OTHER]: "export const note = true;\n",
    },
  });
  host.setMedia(CODEX_ID, CODEX_IMAGE, PIXEL_RED);
  const projections = new Map([
    [CLAUDE_ID, claude],
    [CODEX_ID, codex],
  ]);
  const unsubscribe = host.onReceived("switch-session", (message) => {
    const projection = projections.get(String(message.id));
    if (projection === undefined) {
      throw new Error(`unexpected switch target ${String(message.id)}`);
    }
    // HostCore.SwitchToSlot publishes the editor owner/tabs before flipping the active session rail row.
    host.pushToWeb(editorSession(projection));
    host.pushToWeb({ type: "session-list", sessions: sessions(projection.id) });
  });

  try {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitForMessage("ready");
    host.pushToWeb(editorSession(claude));
    host.pushToWeb({ type: "session-list", sessions: sessions(CLAUDE_ID) });
    await expect(page.locator(".editor")).toHaveAttribute("data-ready", "true", {
      timeout: 60_000,
    });
    await expect(page.locator(".editor")).toHaveAttribute(
      "data-active-file",
      /[\\/]workspace[\\/]claude[\\/]active\.ts$/,
    );
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

    expect(
      Math.max(...claudeToCodex),
      `Claude -> Codex switch exceeded ${SWITCH_BUDGET_MS}ms: ${JSON.stringify(claudeToCodex)}`,
    ).toBeLessThan(SWITCH_BUDGET_MS);
    expect(
      Math.max(...codexToClaude),
      `Codex -> Claude switch exceeded ${SWITCH_BUDGET_MS}ms: ${JSON.stringify(codexToClaude)}`,
    ).toBeLessThan(SWITCH_BUDGET_MS);
  } finally {
    unsubscribe();
    await host.close();
  }
});
