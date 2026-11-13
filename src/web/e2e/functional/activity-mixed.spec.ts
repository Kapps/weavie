import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

test("settled tools, questions, and subagents fold together and tab output expands independently", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "mixed-activity");
  await surface.locator(".pane-fullscreen").click();
  const workspace = sessionWorktrees(weavie.workspace)[0]!;
  await writeFile(join(workspace, "hold-activity-question"), "");
  await writeFile(join(workspace, "hold-activity-subagent"), "");
  await submitAcpDraft(surface, "activity-mixed");
  const earlier = surface.locator(
    ".agent-transcript > .agent-virtual-row > .agent-entry-updates .agent-updates-toggle",
  );
  await expect(earlier).toHaveAttribute("aria-expanded", "false");
  await expect(surface.locator(".agent-entry-activity")).toHaveCount(0);
  await expect(surface.locator(".agent-entry-message.agent-tone-assistant")).toHaveCount(0);

  await writeFile(join(workspace, "release-activity-question"), "");
  const question = surface.locator(".agent-entry-question");
  await expect(question).toHaveClass(/agent-entry-waiting/);
  await question.locator(".agent-choice-row").first().click();
  const child = surface.locator("[data-agent-subagent]");
  await expect(child).toHaveAttribute("data-state", "running");
  await expect(child).toContainText("Explore");
  await writeFile(join(workspace, "release-activity-subagent"), "");
  await expect(surface.locator(".agent-entry-result")).toHaveText(/Ready for review/);
  await expect(earlier).toHaveCount(1);
  await expect(surface.locator(".agent-entry-activity")).toHaveCount(0);
  await expect(question).toHaveCount(0);
  await expect(child).toHaveCount(0);
  await expect(surface.locator(".agent-tone-error")).toHaveCount(0);

  await earlier.click();
  await question.getByText("show prompt and options", { exact: true }).click();
  await expect(question.locator(".agent-input-choice.chosen")).toContainText(
    "Latest reasoning summary",
  );
  await expect(question).not.toHaveClass(/agent-entry-waiting/);
  await question.getByText("hide prompt", { exact: true }).click();
  await expect(child).toHaveAttribute("data-state", "completed");
  const trailing = surface.locator(".agent-entry-activity").last();
  await expect(trailing).not.toHaveClass(/agent-tone-error/);
  await trailing.locator("summary").click();
  await expect(trailing.locator(".agent-activity-step")).toContainText("Trailing verification");
  await expect(trailing.locator(".agent-step-status")).toHaveText("failed");
  await child.locator(".agent-entry-toggle").click();
  await child.getByRole("button", { name: /Earlier updates/ }).click();
  const paneActivity = child.locator(".agent-entry-activity");
  await expect(paneActivity.locator(".agent-activity-details")).not.toHaveAttribute("open");
  const cardHeader = child.locator(".agent-entry-head").first();
  const headerTop = (await cardHeader.boundingBox())!.y;
  await child.locator(".agent-entry-toggle").click();
  await expect(paneActivity).toBeHidden();
  expect(Math.abs((await cardHeader.boundingBox())!.y - headerTop)).toBeLessThanOrEqual(1);
  await child.locator(".agent-entry-toggle").click();
  await expect(paneActivity).toBeVisible();
  expect(Math.abs((await cardHeader.boundingBox())!.y - headerTop)).toBeLessThanOrEqual(1);

  await surface.locator(".pane-fullscreen").click();
  await child.getByRole("button", { name: "Open", exact: true }).click();
  const tab = page.locator(".editor-agent-view");
  await expect(tab).toContainText("Reviewed the build output.");
  const tabActivity = tab.locator(".agent-entry-activity");
  await tabActivity.locator("summary").first().click();
  await expect(tabActivity.locator(".agent-activity-step")).toContainText("Read build output");
  await tabActivity.locator(".agent-tool-output-details > summary").click();
  await expect(tabActivity.locator(".agent-tool-output")).toContainText(
    "Build output: 12 checks passed.",
  );
  await expect(paneActivity.locator(".agent-activity-details")).not.toHaveAttribute("open");
  await expect(paneActivity.locator(".agent-activity-step")).toHaveCount(0);
  await paneActivity.locator("summary").first().click();
  await expect(paneActivity.locator(".agent-activity-step")).toContainText("Read build output");
  await tabActivity.locator("summary").first().click();
  await expect(paneActivity.locator(".agent-activity-step")).toContainText("Read build output");
});
