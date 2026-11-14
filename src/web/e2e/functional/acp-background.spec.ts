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
  await expect(group.locator("[data-background-item]")).toHaveCount(0);
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

test("a background task waits in the collapsed roster until Stop ends it with a notice", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "acp-background-task");
  await submitAcpDraft(surface, "task-held");

  const group = surface.locator("[data-agent-subagents]");
  const row = group.locator("[data-background-item]");
  await expect(group).toContainText("1 running");
  await expect(group.locator(".agent-working-spinner")).toBeVisible();
  await expect(row).toHaveCount(0);
  await expect(surface.locator(".agent-strip")).toHaveCount(0);
  await expect(page.locator(".session-chip.active")).toHaveClass(/status-waiting/);
  await runCommand(page, "Expand or Collapse Subagents");
  await expect(
    group.getByRole("button", { name: "Collapse Subagents", exact: true }),
  ).toBeFocused();
  await expect(row).toHaveAttribute("data-state", "running");
  await expect(row).toContainText("sleep 30");

  await row.getByRole("button", { name: "Stop", exact: true }).click();
  await expect(row).toHaveAttribute("data-state", "stopped");
  await expect(row.getByRole("button", { name: "Stop", exact: true })).toHaveCount(0);
  await expect(group.locator(".agent-working-spinner")).toHaveCount(0);
  await expect(surface.locator(".agent-entry-notice")).toContainText("Task stopped by user");
});

test("a long task name is cut short, keeping Stop inside the roster", async ({ page }) => {
  const surface = await createAcpSession(page, "acp-background-long");
  await submitAcpDraft(surface, "task-long");
  await surface.getByRole("button", { name: "Expand Subagents", exact: true }).click();
  const stop = surface
    .locator("[data-agent-subagents] [data-background-item]")
    .getByRole("button", { name: "Stop", exact: true });
  await expect(stop).toBeVisible();
  const inside = await stop.evaluate((button) => {
    const roster = button.closest("[data-agent-subagents]")!;
    return (
      roster.scrollWidth <= roster.clientWidth &&
      button.getBoundingClientRect().right <= roster.getBoundingClientRect().right
    );
  });
  expect(inside).toBe(true);
});

test("a workflow renders a card and is listed in the roster with Stop", async ({
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

  await runCommand(page, "Expand or Collapse Subagents");
  const row = surface.locator("[data-agent-subagents] [data-background-item]");
  await expect(row).toHaveAttribute("data-state", "running");
  await expect(row.getByRole("button", { name: "Stop", exact: true })).toBeVisible();

  await signal(weavie.workspace, "release-workflow");
  await expect(card).toHaveAttribute("data-state", "completed");
  await expect(card.getByRole("button", { name: "Stop", exact: true })).toHaveCount(0);
});

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
  const rows = surface.locator("[data-agent-subagents] [data-background-item]");
  await expect(rows).toHaveCount(1);

  await page.reload();
  await surface.getByRole("button", { name: "Expand Subagents", exact: true }).click();
  await expect(rows.first()).toHaveAttribute("data-state", "running");

  await runCommand(page, "Unload Session");
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

  await runCommand(page, "Restart Agent");
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
  await expect(rows.filter({ hasText: "sleep 30" })).toHaveAttribute("data-state", "running");
  await surface.locator("[data-agent-composer] textarea").focus();
  await runCommand(page, "Start Fresh Agent Conversation");
  await expect(listed).toHaveCount(1);
  // Work that finishes while the user looks leaves nothing to stop, so the action goes ahead.
  await signal(weavie.workspace, "release-task");
  await expect(dialog).toBeHidden();
  await expect(surface.locator(".agent-entry-message.agent-tone-user")).toHaveCount(0);
});

test("the agent's own unprompted turn shows in the roster but never asks to be stopped", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "acp-background-turn");
  await submitAcpDraft(surface, "task-turn");
  await surface.getByRole("button", { name: "Expand Subagents", exact: true }).click();
  const rows = surface.locator("[data-agent-subagents] [data-background-item]");
  await expect(rows).toHaveAttribute("data-state", "running");
  await expect(rows).toContainText("Claude is working");

  await runCommand(page, "Restart Agent");
  await expect(page.locator(".toast", { hasText: "Restarted the agent." })).toBeVisible();
  await expect(page.getByRole("dialog")).toHaveCount(0);
});

test("an unprompted turn leaves the roster when it ends", async ({ page, weavie }) => {
  const surface = await createAcpSession(page, "acp-background-turn-ends");
  const group = surface.locator("[data-agent-subagents]");
  const items = group.locator("[data-background-item]");

  await submitAcpDraft(surface, "task-held");
  await surface.getByRole("button", { name: "Expand Subagents", exact: true }).click();
  await expect(items.getByRole("button", { name: "Stop" })).toBeVisible();
  await signal(weavie.workspace, "release-task");
  await expect(items).toHaveAttribute("data-state", "completed");
  await expect(items.getByRole("button", { name: "Stop" })).toHaveCount(0);

  // The next prompt forgets the finished task, so the turn is the roster's only item.
  await submitAcpDraft(surface, "task-turn");
  await expect(items).toHaveCount(1);
  await expect(items).toContainText("Claude is working");
  await signal(weavie.workspace, "release-turn");
  await expect(group).toHaveCount(0);
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
