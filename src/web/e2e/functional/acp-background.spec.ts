import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

test.use({
  fakeScript: {
    steps: [
      { op: "mcp", tool: "setSetting", args: { key: "agent.thoughtDisplay", value: "current" } },
    ],
  },
});

async function signal(workspace: string, name: string): Promise<void> {
  await writeFile(join(sessionWorktrees(workspace)[0]!, name), "");
}

test("subagents group outside the transcript and remain selectable after completion", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "acp-subagent-card");
  await submitAcpDraft(surface, "subagent-grouping");
  const group = surface.locator("[data-agent-subagents]");
  const toggle = group.getByRole("button", { name: "Expand Subagents", exact: true });
  await expect(toggle).toHaveAttribute("aria-expanded", "false");
  await expect(group).toContainText("1 running");
  await expect(surface.locator(".agent-transcript [data-agent-subagent]")).toHaveCount(0);
  await expect(surface.locator(".agent-background-tray [data-background-item]")).toHaveCount(0);
  await surface.getByRole("button", { name: /Earlier updates/ }).click();
  const working = surface.locator(".agent-entry-activity");
  await expect(working).toHaveCount(1);
  await working.locator("summary").click();
  await expect(working.locator(".agent-activity-step")).toHaveCount(2);
  await expect(working).toContainText("Inspect repository");
  await expect(working).toContainText("Verify repository");

  await runCommand(page, "Expand or Collapse Subagents");
  const card = group.locator("[data-agent-subagent]");
  await expect(card).toHaveAttribute("data-state", "running");
  await card.getByRole("button", { name: "Explore", exact: true }).click();
  const tab = page.locator(".editor-tab", { hasText: "Subagent · Explore" });
  const transcript = page.locator(".editor-agent-view");
  await expect(tab).toBeVisible();
  await expect(transcript).toContainText("counting");
  await signal(weavie.workspace, "release-subagent");
  await expect(card).toHaveAttribute("data-state", "completed");
  await expect(
    group.getByRole("button", { name: "Collapse Subagents", exact: true }),
  ).toHaveAttribute("aria-expanded", "true");
  await expect(transcript).toContainText("held subagent finished");
  await tab.hover();
  await tab.locator(".editor-tab-close").click();

  await submitAcpDraft(surface, "subagent-failed");
  const failed = group.locator('[data-agent-subagent][data-state="failed"]');
  await expect(failed).toContainText("Reviewer");
  await expect(failed).toContainText("failed");
  await expect(group.locator("[data-agent-subagent]")).toHaveCount(2);
  await failed.getByRole("button", { name: "Reviewer", exact: true }).click();
  await expect(page.locator(".editor-agent-view")).toContainText("review failed");
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

test("a workflow renders a card, and Focus Agent Strip focuses the strip", async ({
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
  await page.locator(".tb-omnibar-input").fill(">Focus Agent Strip");
  await page
    .locator(".tb-omnibar-row")
    .filter({ has: page.locator(".tb-row-leaf", { hasText: /^Focus Agent Strip$/ }) })
    .click();
  await expect(surface.locator(".agent-background-tray button").first()).toBeFocused();
  await page.keyboard.press("ArrowRight");
  await expect(surface.locator(".agent-background-tray .agent-background-stop")).toBeFocused();

  await signal(weavie.workspace, "release-workflow");
  await expect(card).toHaveAttribute("data-state", "completed");
  await expect(card.getByRole("button", { name: "Stop", exact: true })).toHaveCount(0);
});

test("an overflowing tray fades the edge hiding pills, and roving focus scrolls them into view", async ({
  page,
}) => {
  await page.setViewportSize({ width: 900, height: 720 });
  const surface = await createAcpSession(page, "acp-background-overflow");
  const pills = surface.locator(".agent-background-tray [data-background-item]");
  for (let count = 1; count <= 6; count++) {
    await submitAcpDraft(surface, "task-held");
    await expect(pills).toHaveCount(count);
  }
  const scroller = surface.locator(".agent-background-tray .agent-status-scroll");
  await expect(scroller).toHaveClass(/overflow-right/);
  await expect(scroller).not.toHaveClass(/overflow-left/);

  await runPalette(page, "Focus Agent Strip");
  await expect(surface.locator(".agent-background-tray button").first()).toBeFocused();
  await page.keyboard.press("End");
  const last = pills.last().getByRole("button", { name: "Stop", exact: true });
  await expect(last).toBeFocused();
  await expect(scroller).toHaveClass(/overflow-left/);
  await expect(scroller).not.toHaveClass(/overflow-right/);
  const inside = await last.evaluate((button) => {
    const pill = button.getBoundingClientRect();
    const view = button.closest(".agent-status-scroll")!.getBoundingClientRect();
    return pill.left >= view.left && pill.right <= view.right;
  });
  expect(inside).toBe(true);
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
  await surface.getByRole("button", { name: "Expand Subagents", exact: true }).click();
  await expect(card).toHaveAttribute("data-state", "running");
  await submitAcpDraft(surface, "task-held");
  const tray = surface.locator(".agent-background-tray [data-background-item]");
  await expect(tray).toHaveCount(1);

  await page.reload();
  await surface.getByRole("button", { name: "Expand Subagents", exact: true }).click();
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
  // The dialog closes on the click; the restart lands when its command reports back.
  await expect(page.locator(".toast", { hasText: "Restarted the agent." })).toBeVisible();
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

test("the agent's own unprompted turn shows in the tray but never asks to be stopped", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "acp-background-turn");
  await submitAcpDraft(surface, "task-turn");
  const tray = surface.locator(".agent-background-tray [data-background-item]");
  await expect(tray).toHaveAttribute("data-state", "running");
  await expect(tray).toContainText("Claude is working");

  await runPalette(page, "Restart Agent");
  await expect(page.locator(".toast", { hasText: "Restarted the agent." })).toBeVisible();
  await expect(page.getByRole("dialog")).toHaveCount(0);
});

test("an unprompted turn leaves the tray when it ends, and Stop never changes the tray's height", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "acp-background-turn-ends");
  const tray = surface.locator(".agent-background-tray");
  const items = tray.locator("[data-background-item]");

  await submitAcpDraft(surface, "task-held");
  await expect(items.getByRole("button", { name: "Stop" })).toBeVisible();
  const stoppable = (await tray.boundingBox())!.height;
  await signal(weavie.workspace, "release-task");
  await expect(items).toHaveAttribute("data-state", "completed");
  await expect(items.getByRole("button", { name: "Stop" })).toHaveCount(0);
  expect((await tray.boundingBox())!.height).toBe(stoppable);

  // The next prompt forgets the finished task, so the turn is the tray's only item.
  await submitAcpDraft(surface, "task-turn");
  await expect(items).toHaveCount(1);
  await expect(items).toContainText("Claude is working");
  await signal(weavie.workspace, "release-turn");
  await expect(tray).toHaveCount(0);
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
    await expect(
      surface.getByRole("button", { name: "Expand Subagents", exact: true }),
    ).toHaveAttribute("aria-expanded", "false");
    const request = surface.locator("[data-agent-subagent] .agent-entry-request");
    const input = request.locator(".agent-entry-main > .agent-entry-text");
    await expect(input).toContainText("README.md");
    await expect(input).not.toContainText("old_string");

    await request.getByText("show input").click();
    await expect(request.locator(".agent-request-details .agent-entry-text")).toContainText(
      '"old_string": "# soak"',
    );
    await request.getByRole("button", { name: "Allow" }).click();
    await surface.getByRole("button", { name: "Expand Subagents", exact: true }).click();
    await expect(surface.locator("[data-agent-subagent]")).toHaveAttribute(
      "data-state",
      "completed",
    );

    await submitAcpDraft(surface, "subagent-permission-outside");
    const outside = surface.locator("[data-agent-subagent] .agent-entry-request").last();
    const path = outside.locator(".agent-entry-main > .agent-entry-text");
    await expect(path).toHaveText(/^~[\\/].*settings\.json$/);
    await expect(path).toHaveAttribute("title", /^[^~].*fake-agent.*settings\.json$/);
    await outside.getByRole("button", { name: "Allow" }).click();

    await submitAcpDraft(surface, "permission");
    const primary = surface.locator(".agent-entry-request").last();
    await expect(primary.locator(".agent-entry-main > .agent-entry-text")).toHaveText(
      "npm test -- --run",
    );
    await expect(primary.getByText("show input")).toBeVisible();
  });
});
