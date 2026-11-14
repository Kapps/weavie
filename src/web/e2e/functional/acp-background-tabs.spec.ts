import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";

test("selecting subagents and tasks in the roster opens their transcripts and results", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "acp-background-tabs");
  const rows = surface.locator("[data-agent-subagents] [data-background-item]");
  await submitAcpDraft(surface, "subagent-held");
  await surface.getByRole("button", { name: "Expand Subagents", exact: true }).click();
  const subagent = surface
    .locator("[data-agent-subagent]")
    .getByRole("button", { name: "Explore", exact: true });
  await expect(subagent).toHaveAttribute("title", /^Open Card in Tab/);
  await subagent.click();
  await expect(page.locator(".editor-tab", { hasText: "Subagent · Explore" })).toBeVisible();
  await expect(page.locator(".editor-agent-view")).toContainText("counting");

  await submitAcpDraft(surface, "task-failed");
  const task = rows.filter({ hasText: "Consult Astra" });
  await expect(task).toHaveAttribute("data-state", "failed");
  await task.getByRole("button", { name: /Consult Astra/ }).click();
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
