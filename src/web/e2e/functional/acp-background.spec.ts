import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

async function signal(workspace: string, name: string): Promise<void> {
  await writeFile(join(sessionWorktrees(workspace)[0]!, name), "");
}

test("a subagent renders as a live card that collapses when it finishes", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "acp-subagent-card");
  await submitAcpDraft(surface, "subagent-held");

  const card = surface.locator("[data-agent-subagent]");
  await expect(card).toHaveAttribute("data-state", "running");
  await expect(card.locator(".agent-entry-summary").first()).toHaveText("Explore");
  await expect(card).toContainText("counting");
  await expect(card.locator(".agent-entry-toggle")).toHaveAttribute("aria-expanded", "true");
  await submitAcpDraft(surface, "/btw side question");
  await expect(surface.locator(".agent-aside")).toContainText("echo: side question");

  await signal(weavie.workspace, "release-subagent");
  await expect(card).toHaveAttribute("data-state", "completed");
  await expect(card.locator(".agent-entry-toggle")).toHaveAttribute("aria-expanded", "false");

  await card.getByRole("button", { name: "Open" }).click();
  const tab = page.locator(".editor-tab", { hasText: "Subagent · Explore" });
  await expect(tab).toBeVisible();
  const transcript = page.locator(".editor-subagent");
  await expect(transcript).toContainText("held subagent finished");
  await expect(transcript.locator(".agent-entry-status").first()).toContainText("completed");
  await tab.hover();
  await tab.locator(".editor-tab-close").click();
  await expect(tab).toHaveCount(0);

  await submitAcpDraft(surface, "subagent-failed");
  const failed = surface.locator('[data-agent-subagent][data-state="failed"]');
  await expect(failed).toHaveClass(/agent-tone-error/);
  await expect(failed.locator(".agent-entry-status")).toContainText("failed");
});

test("a background task waits in the tray until Stop ends it with a notice", async ({ page }) => {
  const surface = await createAcpSession(page, "acp-background-task");
  await submitAcpDraft(surface, "task-held");

  const pill = surface.locator(".agent-background-tray [data-background-item]");
  await expect(pill).toHaveAttribute("data-state", "running");
  await expect(pill).toContainText("sleep 30");
  await expect(surface.locator(".agent-background-tray")).toHaveAttribute("role", "toolbar");
  await expect(page.locator(".session-chip.active")).toHaveClass(/status-waiting/);

  await pill.getByRole("button", { name: "Stop", exact: true }).click();
  await expect(pill).toHaveAttribute("data-state", "stopped");
  await expect(pill.getByRole("button", { name: "Stop", exact: true })).toHaveCount(0);
  await expect(surface.locator(".agent-entry-notice")).toContainText("Task stopped by user");
});

test("a workflow renders a card, and Show Background Work focuses the tray", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "acp-background-workflow");
  await submitAcpDraft(surface, "workflow-held");

  const card = surface.locator("[data-agent-workflow]");
  await expect(card).toHaveAttribute("data-state", "running");
  await expect(card).toContainText("code-review");
  await expect(card).toContainText("3 tools");
  await expect(card.getByRole("button", { name: "Stop", exact: true })).toBeVisible();

  await page.keyboard.press("ControlOrMeta+Shift+p");
  await page.locator(".tb-omnibar-input").fill(">Show Background Work");
  await page
    .locator(".tb-omnibar-row")
    .filter({ has: page.locator(".tb-row-leaf", { hasText: /^Show Background Work$/ }) })
    .click();
  await expect(surface.locator(".agent-background-tray button").first()).toBeFocused();
  await page.keyboard.press("ArrowRight");
  await expect(surface.locator(".agent-background-tray .agent-background-stop")).toBeFocused();

  await signal(weavie.workspace, "release-workflow");
  await expect(card).toHaveAttribute("data-state", "completed");
  await expect(card.getByRole("button", { name: "Stop", exact: true })).toHaveCount(0);
});

async function runPalette(page: import("@playwright/test").Page, title: string): Promise<void> {
  await page.keyboard.press("ControlOrMeta+Shift+p");
  await page.locator(".tb-omnibar-input").fill(`>${title}`);
  await page
    .locator(".tb-omnibar-row")
    .filter({ has: page.locator(".tb-row-leaf", { hasText: new RegExp(`^${title}$`) }) })
    .click();
}

test("running work survives a page reload, and stopping actions confirm against the live work", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "acp-background-guard");
  await submitAcpDraft(surface, "subagent-held");
  const card = surface.locator("[data-agent-subagent]");
  await expect(card).toHaveAttribute("data-state", "running");
  await submitAcpDraft(surface, "task-held");
  const tray = surface.locator(".agent-background-tray [data-background-item]");
  await expect(tray).toHaveCount(2);

  await page.reload();
  await expect(tray.first()).toHaveAttribute("data-state", "running");

  await runPalette(page, "Unload Session");
  const dialog = page.getByRole("dialog");
  const listed = dialog.locator(".confirm-background-list li");
  await expect(dialog).toContainText("Stop background work?");
  await expect(listed).toHaveCount(2);
  await expect(listed.first()).toContainText("Explore");
  await expect(listed.first()).toContainText("subagent · running");
  await expect(dialog.getByRole("button", { name: "Stop and unload" })).toBeVisible();
  await expect(dialog.getByRole("button", { name: "Keep running" })).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(dialog).toBeHidden();
  await expect(card).toHaveAttribute("data-state", "running");

  await runPalette(page, "Restart Agent");
  await expect(listed).toHaveCount(2);
  await signal(weavie.workspace, "release-subagent");
  await expect(listed).toHaveCount(1);
  await expect(listed).toContainText("sleep 30");
  await dialog.getByRole("button", { name: "Stop and restart" }).click();
  await expect(dialog).toBeHidden();
  await expect(card).toHaveAttribute("data-state", "completed");
  await expect(surface.locator("[data-agent-composer]")).toHaveAttribute(
    "data-agent-controls-ready",
    "true",
  );

  await submitAcpDraft(surface, "task-held");
  await expect(tray).toHaveAttribute("data-state", "running");
  await surface.locator("[data-agent-composer] textarea").focus();
  await runPalette(page, "Start Fresh Agent Conversation");
  await expect(listed).toHaveCount(1);
  // Work that finishes while the user looks leaves nothing to stop, so the action goes ahead.
  await signal(weavie.workspace, "release-task");
  await expect(dialog).toBeHidden();
  await expect(surface.locator(".agent-entry-message.agent-tone-user")).toHaveCount(0);
});

test.describe("with approvals", () => {
  test.use({
    fakeScript: {
      steps: [
        { op: "mcp", tool: "setSetting", args: { key: "agent.allowAllPermissions", value: false } },
      ],
    },
  });

  test("approvals show the telling part of their input, with the raw input behind show input", async ({
    page,
  }) => {
    const surface = await createAcpSession(page, "acp-subagent-approval");
    await submitAcpDraft(surface, "subagent-permission");
    const request = surface.locator("[data-agent-subagent] .agent-entry-request");
    const input = request.locator(".agent-entry-main > .agent-entry-text");
    await expect(input).toContainText("README.md");
    await expect(input).not.toContainText("old_string");

    await request.getByText("show input").click();
    await expect(request.locator(".agent-request-details .agent-entry-text")).toContainText(
      '"old_string": "# soak"',
    );
    await request.getByRole("button", { name: "Allow" }).click();
    await expect(surface.locator("[data-agent-subagent]")).toHaveAttribute(
      "data-state",
      "completed",
    );

    await submitAcpDraft(surface, "permission");
    const primary = surface.locator(".agent-entry-request").last();
    await expect(primary.locator(".agent-entry-main > .agent-entry-text")).toHaveText(
      "npm test -- --run",
    );
    await expect(primary.getByText("show input")).toBeVisible();
  });
});
