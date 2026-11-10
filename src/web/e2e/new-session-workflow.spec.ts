import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { devices, expect, type Page } from "@playwright/test";
import { CommandIds, type CommandInfo } from "../src/commands/types";
import { test } from "./harness/network-fixtures";
import { MockHost, mockSession } from "./mock-host";

const distDir = join(dirname(fileURLToPath(import.meta.url)), "..", "dist");
const DRAFT =
  "the WebM video in the review pane fails to load whenever the diff is reopened twice in a row";
const CATALOG = {
  workflows: [
    { id: "broken", description: "", error: "run needs exactly one of prompt or skill" },
    { id: "quick-fix", description: "A small, well-understood change.", error: null },
    { id: "spec-driven", description: "Requirements, design, and a task list.", error: null },
  ],
  default: null,
  defaultProblem: null,
};

test.beforeAll(() => {
  if (!existsSync(join(distDir, "index.html"))) {
    throw new Error(
      `built app not found at ${distDir}; run \`pnpm run build\` before the e2e tests`,
    );
  }
});

async function openSessions(page: Page, host: MockHost): Promise<void> {
  const create: CommandInfo = {
    id: CommandIds.newSession,
    title: "New Session",
    runsIn: "core",
    owner: "backend",
    executionLane: "weavie.session.lifecycle",
    scope: "host",
    keys: [],
    description: "",
    aliases: [],
    showInPalette: false,
  };
  host.setSessions([mockSession("main", "main", "acp")]);
  host.onHost("request", "git", "branches", (request) => host.respond(request, ["main"]));
  host.onHost("request", "sessionCreation", "workflows", (request) =>
    host.respond(request, CATALOG),
  );
  await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
  await host.waitUntilConnected();
  host.publishHost("commands", "catalog", { commands: [create], keybindings: [] });
}

test("the composer suggests a workflow once, lets the keyboard change it, and starts the session in it", async ({
  page,
}) => {
  const host = await MockHost.start({ distDir });
  try {
    host.onHost("request", "sessionCreation", "previewBranch", (request) =>
      host.respond(request, { branch: "bug/webm-reload", error: null, needsMoreDetail: false }),
    );
    await page.setViewportSize({ width: 1200, height: 800 });
    await openSessions(page, host);
    await page.locator(".session-rail-add").click();
    const modal = page.getByRole("dialog", { name: "Sessions" });
    const inbox = page.locator(".session-inbox");
    const workflow = inbox.getByRole("button", { name: "Workflow" });
    await expect(workflow).toHaveText("No workflow");
    await expect(workflow).toBeEnabled();

    // One suggestion for the settled draft: it fills the field, and more typing never asks again.
    const suggestion = host.waitForHost("request", "sessionCreation", "suggestWorkflow");
    await inbox.getByRole("textbox", { name: "Prompt for a new session" }).fill(DRAFT);
    const asked = await suggestion;
    expect(asked.payload).toEqual({ sourceId: null, prompt: DRAFT });
    await expect(workflow).toContainText("Suggesting…");
    host.respond(asked, { applies: true, workflow: "quick-fix", error: null });
    await expect(workflow).toHaveText("quick-fix");

    // Reached by Tab from the branch row; the menu takes the keys while it is open, the dialog after.
    await inbox.getByRole("button", { name: "Suggest a branch name again" }).focus();
    await page.keyboard.press("Tab");
    await expect(workflow).toBeFocused();
    await page.keyboard.press("ArrowDown");
    const menu = page.getByRole("listbox", { name: "Workflow" });
    await expect(menu.getByRole("option")).toHaveText([
      "No workflow",
      /^broken/,
      /^quick-fix.*A small, well-understood change\.$/,
      /^spec-driven/,
    ]);
    await expect(menu.getByRole("option", { name: /quick-fix/ })).toHaveAttribute(
      "aria-selected",
      "true",
    );
    const broken = menu.getByRole("option", { name: /broken/ });
    await expect(broken).toHaveAttribute("aria-disabled", "true");
    await expect(broken).toHaveAttribute("title", "run needs exactly one of prompt or skill");
    await expect(broken.locator(".control-menu-option-reason")).toBeHidden();
    await page.keyboard.press("Escape");
    await expect(menu).toBeHidden();
    await expect(modal).toBeVisible();

    // An unavailable workflow can't be picked; the next one can.
    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("ArrowUp");
    await page.keyboard.press("Enter");
    await expect(menu).toBeVisible();
    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("Enter");
    await expect(menu).toBeHidden();
    await expect(workflow).toHaveText("spec-driven");
    await expect(workflow).toBeFocused();
    expect(host.received.filter((message) => message.name === "suggestWorkflow")).toHaveLength(1);

    const invocation = host.waitForHost("request", "sessions", "invoke");
    await inbox.getByRole("button", { name: "Start", exact: true }).click();
    expect((await invocation).payload).toMatchObject({
      id: CommandIds.newSession,
      args: {
        branch: "bug/webm-reload",
        base: "main",
        existing: false,
        prompt: DRAFT,
        attachments: [],
        workflow: "spec-driven",
      },
    });
  } finally {
    await host.close();
  }
});

test("touch reads why a workflow is unavailable in its row", async ({ browser }) => {
  const host = await MockHost.start({ distDir });
  const context = await browser.newContext({ ...devices["Pixel 7"] });
  try {
    const page = await context.newPage();
    await openSessions(page, host);
    const inbox = page.locator(".session-inbox");
    await inbox.getByRole("button", { name: "Workflow" }).tap();

    await expect(
      page.getByRole("option", { name: /broken/ }).locator(".control-menu-option-reason"),
    ).toHaveText("run needs exactly one of prompt or skill");
  } finally {
    await context.close();
    await host.close();
  }
});
