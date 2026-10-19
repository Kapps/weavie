import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";

// FLAKE 2026-10-19 ~16:21Z (macOS shard 2/6, the subagent's tab): the child's "counting" message never showed in the card.
// https://github.com/Kapps/weavie/actions/runs/39283401373/job/118165293632
// FIX: the host sent it (the trace's WebSocket frames carry it in the first paneBatch). A batch landing on an empty
// transcript skipped the accumulator's per-index bookkeeping, so its unfinished stream was never materialized and kept
// its empty placeholder text. Linux splits the turn into several batches and never hit it; a slow runner does not.
test("clicking tray work opens it in a tab: a subagent's transcript, a task's state and result", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "acp-background-tabs");
  const pills = surface.locator(".agent-background-tray [data-background-item]");
  await submitAcpDraft(surface, "subagent-held");
  const subagent = pills.filter({ hasText: "Explore" }).locator(".agent-status-segment").first();
  await expect(subagent).toHaveAttribute("title", /^Open .* in a tab/);
  await subagent.click();
  await expect(page.locator(".editor-tab", { hasText: "Subagent · Explore" })).toBeVisible();
  await expect(page.locator(".editor-agent-view")).toContainText("counting");

  await submitAcpDraft(surface, "task-failed");
  const task = pills.filter({ hasText: "Consult Astra" });
  await expect(task).toHaveAttribute("data-state", "failed");
  await task.locator(".agent-status-segment").first().click();
  await expect(page.locator(".editor-tab", { hasText: "Task · Consult Astra" })).toBeVisible();
  const view = page.locator('.editor-agent-view [data-agent-task][data-state="failed"]');
  await expect(view.locator(".agent-entry-status")).toContainText("failed");
  await expect(view).toContainText("Task · monitor");
  await expect(view.locator("[data-task-summary]")).toHaveText("No agent named Astra");
  await expect(view.locator("[data-task-tool]")).toContainText("weavie/consultAgent");
  await expect(view.locator("[data-task-tool]")).toContainText("consult tool error");

  await view.locator(".agent-task-facts a").click();
  await expect(page.locator(".editor-tab.active")).toContainText(".output");
});
