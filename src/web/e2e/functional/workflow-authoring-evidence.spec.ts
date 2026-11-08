import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";
import {
  createWorkflowSession,
  workflowReceipt,
  workflowTool,
  writeWorkflowSource,
} from "../harness/workflows";

const SOURCE = `id: advisory-evidence
description: Warnings remain an authoring judgment.
stages:
  - id: review
    run:
      prompt: Review the release notes.
      outputs: { document: files, directory: text }
    then:
      ask: Does README.md describe the release notes?
      choices: { accept: accepted, revise: review }
  - id: accepted
    run: { prompt: The release notes were accepted. }
`;
const CONTENTS = "# Release notes\nThe release checklist is complete.\n";

test.use({ inference: "workflow" });

test("evidence warnings allow saving and running, and an explicit files reference removes them", async ({
  page,
  weavie,
}) => {
  const { surface, worktree, run } = await createWorkflowSession(
    page,
    weavie.home,
    weavie.workspace,
    "claude",
  );
  await writeFile(join(worktree, "README.md"), CONTENTS);
  const validation = await workflowTool(surface, "validateWorkflow", { text: SOURCE });
  expect(validation).toContain("Valid.");
  expect(validation).toContain("path-like text");
  expect(validation).toContain("contents will not be read");
  expect(validation).toContain("stages[0].then.ask");
  expect(validation).toMatch(/line \d+, column \d+/);

  await writeWorkflowSource(worktree, "advisory-evidence", SOURCE);
  const start = await workflowTool(surface, "runCommand", {
    id: "weavie.workflow.run",
    args: { workflow: "advisory-evidence", inputs: {} },
  });
  const id = /Started run (r-[a-f0-9]+)/.exec(start)![1]!;
  await workflowTool(surface, "completeStage", {
    outputs: { document: ["README.md"], directory: "." },
    summary: "The release checklist is complete.",
  });
  await expect(run).toHaveText("advisory-evidence · accepted");
  expect((await workflowReceipt(worktree)).evidence.files).toEqual([]);
  await workflowTool(surface, "runCommand", {
    id: "weavie.workflow.cancel",
    args: { run: id },
  });

  const corrected = SOURCE.replace("README.md", "{{review.document}}");
  const valid = await workflowTool(surface, "validateWorkflow", { text: corrected });
  expect(valid).toMatch(/validateWorkflow: Valid\.\s*$/);
  await writeWorkflowSource(worktree, "advisory-evidence", corrected);
  await workflowTool(surface, "runCommand", {
    id: "weavie.workflow.run",
    args: { workflow: "advisory-evidence", inputs: {} },
  });
  await workflowTool(surface, "completeStage", {
    outputs: { document: ["README.md"], directory: "." },
    summary: "The release checklist is complete.",
  });
  await expect(run).toHaveText("advisory-evidence · accepted");
  expect((await workflowReceipt(worktree)).evidence.files).toEqual([
    { path: "README.md", text: CONTENTS },
  ]);
});

test("model if and composed paths warn, user decisions do not, and structural errors block runs", async ({
  page,
  weavie,
}) => {
  const { surface, worktree, run } = await createWorkflowSession(
    page,
    weavie.home,
    weavie.workspace,
    "claude",
  );
  const composed = await workflowTool(surface, "validateWorkflow", {
    text: SOURCE.replace("README.md", "{{review.directory}}/README.md"),
  });
  expect(composed).toContain("Valid.");
  expect(composed).toContain("path-like text");
  expect(composed).toContain("contents will not be read");

  const condition = await workflowTool(surface, "validateWorkflow", {
    text: SOURCE.replace("    run:", "    if: Does README.md exist?\n    run:"),
  });
  expect(condition).toContain("Valid.");
  expect(condition).toContain("stages[0].if");
  expect(condition).toContain("path-like text");

  const userOnly = SOURCE.replace("      choices:", "      by: user\n      choices:").replace(
    "    run:",
    '    if: { ask: "Does README.md exist?", by: user }\n    run:',
  );
  const userValidation = await workflowTool(surface, "validateWorkflow", { text: userOnly });
  expect(userValidation).toMatch(/validateWorkflow: Valid\.\s*$/);

  const invalid = SOURCE.replace("    then:", "    surprise: true\n    then:");
  const invalidValidation = await workflowTool(surface, "validateWorkflow", { text: invalid });
  expect(invalidValidation).toContain("Invalid:");
  expect(invalidValidation).toContain("surprise");
  await writeWorkflowSource(worktree, "advisory-evidence", invalid);
  await submitAcpDraft(
    surface,
    `mcp:runCommand:${JSON.stringify({
      id: "weavie.workflow.run",
      args: { workflow: "advisory-evidence", inputs: {} },
    })}`,
  );
  const reply = surface.locator(".agent-entry-message.agent-tone-assistant").last();
  await expect(reply).toContainText("runCommand failed:");
  await expect(reply).toContainText("surprise");
  await expect(run).not.toBeVisible();
});

test("live authoring prompt and schema describe observable decisions and advisory evidence warnings", async ({
  page,
  weavie,
}) => {
  const { surface } = await createWorkflowSession(page, weavie.home, weavie.workspace, "claude");
  const guidance = await workflowTool(surface, "runPrompt", { name: "author-workflow" });
  for (const rule of [
    /observable criteria/i,
    /one concern/i,
    /flags for judgment/i,
    /may ignore/i,
    /current text/i,
    /no tools/i,
    /conversation/i,
    /workflow\.decisionConfidence/,
    /description/,
    /confidence:/,
  ])
    expect(guidance).toMatch(rule);
  const schema = await workflowTool(surface, "getWorkflowSchema", {});
  for (const rule of [/observable/i, /one concern/i, /confidence/, /description/, /files/]) {
    expect(schema).toMatch(rule);
  }
});
