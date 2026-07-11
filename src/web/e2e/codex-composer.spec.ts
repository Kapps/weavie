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
        options: [
          { id: "gpt-5.5", label: "GPT-5.5", description: "Frontier model." },
          { id: "gpt-5.4-mini", label: "GPT-5.4 mini", description: "Fast model." },
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
      },
      {
        id: "skill:review-pr",
        name: "review-pr",
        description: "Review a pull request.",
        commandId: null,
        insertText: "Review the current PR.",
      },
    ],
  },
};

const userMessage = (text: string) => ({
  type: "agent-pane",
  slot: "cx",
  workspace: "/repo",
  message: { type: "user-message", providerId: "codex", text },
});

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

  test("status line shows the model, approvals, and sandbox", async ({ page }) => {
    await mountCodex(page);

    const segments = page.locator(".agent-status-segment");
    await expect(segments).toHaveCount(3);
    await expect(segments.nth(0)).toContainText("Model");
    await expect(segments.nth(0)).toContainText("GPT-5.5");
    await expect(segments.nth(1)).toContainText("On request");
    await expect(segments.nth(2)).toContainText("Workspace write");
    await page.screenshot({ path: join(shotsDir, "01-status-line.png") });
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

  test("typing / opens the slash menu and a skill inserts its prompt", async ({ page }) => {
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

    // Narrow to the skill and accept it; its insertText replaces the slash query in the draft.
    await page.keyboard.type("rev");
    await expect(menu.locator(".agent-slash-option")).toHaveCount(1);
    await page.keyboard.press("Enter");
    await expect(menu).toBeHidden();
    await expect(textarea).toHaveValue("Review the current PR.");
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
