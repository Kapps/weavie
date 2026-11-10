import { existsSync } from "node:fs";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { clickIntoEditor, runCommand, showParked, typeInEditor } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";
import { workflowTool, writeWorkflow } from "../harness/workflows";

test("workflow documents cross worktrees, open and save in the editor, survive reload, and clean up with the last session", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "feature-plan");
  const parentWorktree = sessionWorktrees(weavie.workspace)[0]!;
  await writeWorkflow(
    parentWorktree,
    "feature-plan",
    `  - id: draft
    run:
      prompt: "Shared documents: {{run.documents}}"
      outputs: { monitoring: files }
`,
  );
  await writeWorkflow(
    parentWorktree,
    "feature-task",
    `  - id: implement
    run: { prompt: "Read and update {{run.documents}}/monitoring.md", outputs: { monitoring: files } }
`,
  );

  await workflowTool(surface, "runCommand", {
    id: "weavie.workflow.run",
    args: { workflow: "feature-plan" },
  });
  const declaration = surface.locator(".agent-entry-message.agent-tone-assistant", {
    hasText: "echo: Shared documents:",
  });
  await expect(declaration).toBeVisible();
  const documents = (await declaration.textContent())!.split("Shared documents: ")[1]!.trim();
  const monitoring = join(documents, "monitoring.md");
  await submitAcpDraft(surface, `fs:${monitoring}`);
  await expect(surface.locator(".agent-entry-message.agent-tone-assistant").last()).toContainText(
    "fs: written through ACP",
  );
  expect(await readFile(monitoring, "utf8")).toBe("written through ACP");
  expect(documents.startsWith(parentWorktree)).toBe(false);

  await workflowTool(surface, "openFile", { filePath: monitoring, focus: true });
  await clickIntoEditor(page);
  await page.keyboard.press("ControlOrMeta+a");
  const plan = "# Shared monitoring log\n\n- Feature plan approved in the parent session.\n";
  await typeInEditor(page, plan);
  await page.keyboard.press("ControlOrMeta+s");
  await expect.poll(() => readFile(monitoring, "utf8")).toBe(plan);

  await workflowTool(surface, "runCommand", {
    id: "weavie.session.new",
    args: {
      branch: "feature-task",
      base: "main",
      agentProviderId: "fake-acp",
      workflow: "feature-task",
    },
  });
  await page.locator('.session-chip[title^="feature-task —"]').click();
  await expect(surface.locator(".pane-head .pane-run")).toHaveText("feature-task · implement");
  await expect(surface.locator(".agent-entry-message.agent-tone-assistant").last()).toContainText(
    `Read and update ${documents}/monitoring.md`,
  );
  const childWorktree = sessionWorktrees(weavie.workspace).find((path) => path !== parentWorktree)!;
  expect(documents.startsWith(childWorktree)).toBe(false);

  await workflowTool(surface, "openFile", { filePath: monitoring, focus: true });
  await expect(page.locator(".view-lines")).toContainText("Feature plan approved");
  await clickIntoEditor(page);
  await page.keyboard.press("ControlOrMeta+End");
  const update = "- PR session implemented the first task.\n";
  await typeInEditor(page, update);
  await page.keyboard.press("ControlOrMeta+s");
  await expect.poll(() => readFile(monitoring, "utf8")).toBe(plan + update);

  await runCommand(page, "Unload Session");
  await showParked(page);
  await page.locator('.session-chip.unloaded[title^="feature-task —"]').click();
  await expect(surface.locator(".pane-head .pane-run")).toHaveText("feature-task · implement");
  await workflowTool(surface, "openFile", { filePath: monitoring, focus: true });
  await expect(page.locator(".view-lines")).toContainText("PR session implemented");
  const completed = await workflowTool(surface, "completeStage", {
    outputs: { monitoring: [monitoring] },
    summary: "The child updated the shared monitoring log.",
  });
  expect(completed).toContain("workflow is finished");

  await page.locator('.session-chip[title^="feature-plan —"]').click();
  await workflowTool(surface, "openFile", { filePath: monitoring, focus: true });
  await expect(page.locator(".view-lines")).toContainText("PR session implemented");
  expect(await readFile(monitoring, "utf8")).toBe(plan + update);

  await page.locator('.session-chip[title^="feature-task —"]').click();
  await workflowTool(surface, "runCommand", {
    id: "weavie.session.delete",
    args: { id: "feature-plan", force: true },
  });
  await expect(page.locator('.session-chip[title^="feature-plan —"]')).toHaveCount(0);
  expect(await readFile(monitoring, "utf8")).toBe(plan + update);

  await runCommand(page, "Delete Session…");
  await page.getByRole("button", { name: "Delete untracked files…", exact: true }).click();
  await page.getByRole("button", { name: "Confirm delete", exact: true }).click();
  await expect(page.locator('.session-chip[title^="feature-task —"]')).toHaveCount(0);
  await expect.poll(() => existsSync(documents)).toBe(false);
});
