import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";
import { workflowTool, writeWorkflowSource } from "../harness/workflows";

// A stage names the agent that runs it: the session's conversation switches to its model before the stage's turn,
// says so in the transcript and the run tooltip, and returns to the session's own model for a stage naming none.
// See docs/specs/workflow-agent-selection.md.

const TWO_STEP = `id: two-step
description: Implement on Beta with Fast on, then ship on the session's own model.
agents:
  builder: { provider: fake-acp, model: beta, options: { fast: true } }
stages:
  - id: implement
    run: { prompt: implement it, agent: builder }
  - id: ship
    run: { prompt: ship it }
`;

const FOREIGN = `id: foreign
description: Review with another provider's agent.
agents:
  reviewer: { provider: other-acp }
stages:
  - id: review
    run: { prompt: review it, agent: reviewer }
`;

const ROLES = `id: roles
description: Implement as the builder.
stages:
  - id: implement
    run: { prompt: implement it, role: builder }
`;

test("a role runs on the model saved for the session's provider and refuses to start without one", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "roles");
  await writeWorkflowSource(sessionWorktrees(weavie.workspace)[0]!, "roles", ROLES);
  const run = { id: "weavie.workflow.run", args: { workflow: "roles", inputs: {} } };

  await submitAcpDraft(surface, `mcp:runCommand:${JSON.stringify(run)}`);
  await expect(
    surface.getByText(/runCommand failed:.*role .builder. has no agent for fake-acp sessions/),
  ).toBeVisible();
  await workflowTool(surface, "runCommand", {
    id: "weavie.workflow.setRole",
    args: { role: "builder", provider: "fake-acp", agent: { provider: "fake-acp", model: "beta" } },
  });
  // The pane renders quotes typographically, so the saved role is matched quote-agnostic.
  expect(await workflowTool(surface, "listWorkflows", {})).toMatch(
    /.role.:.builder.,.provider.:.fake-acp./,
  );

  await workflowTool(surface, "runCommand", run);
  await expect(surface.getByRole("button", { name: "Model Beta" })).toBeVisible();
  await expect(
    surface.locator(".agent-entry-notice").filter({ hasText: "Workflow: implement runs on Beta" }),
  ).toHaveCount(1);
  await expect(surface.locator(".pane-head .pane-run")).toHaveAttribute(
    "title",
    /^roles · implement on builder · /,
  );
});

test("a stage runs on its agent's model and the next stage returns to the session's own", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "agents");
  const worktree = sessionWorktrees(weavie.workspace)[0]!;
  await writeWorkflowSource(worktree, "two-step", TWO_STEP);
  await writeWorkflowSource(worktree, "foreign", FOREIGN);
  const notices = surface.locator(".agent-entry-notice");
  const run = surface.locator(".pane-head .pane-run");
  await expect(surface.getByRole("button", { name: "Model Alpha" })).toBeVisible();

  await workflowTool(surface, "runCommand", {
    id: "weavie.workflow.run",
    args: { workflow: "two-step", inputs: {} },
  });
  await expect(surface.getByRole("button", { name: "Model Beta" })).toBeVisible();
  await expect(
    notices.filter({ hasText: "Workflow: implement runs on Beta, Fast On" }),
  ).toHaveCount(1);
  await expect(run).toHaveAttribute("title", /^two-step · implement on builder · /);

  await page.reload();
  await expect(surface.getByRole("button", { name: "Model Beta" })).toBeVisible();
  await expect(run).toHaveAttribute("title", /implement on builder/);

  await workflowTool(surface, "completeStage", { outputs: {}, summary: "Implemented." });
  await expect(surface.getByRole("button", { name: "Model Alpha" })).toBeVisible();
  await expect(notices.filter({ hasText: "Workflow: ship runs on Alpha" })).toHaveCount(1);
  await expect(run).toHaveAttribute("title", /^two-step · ship · /);
  await workflowTool(surface, "completeStage", { outputs: {}, summary: "Shipped." });
  await expect(run).toBeHidden();

  await submitAcpDraft(
    surface,
    `mcp:runCommand:${JSON.stringify({ id: "weavie.workflow.run", args: { workflow: "foreign", inputs: {} } })}`,
  );
  await expect(
    surface.getByText(
      /runCommand failed:.*runs only in other-acp sessions; this session runs fake-acp/,
    ),
  ).toBeVisible();
  await expect(run).toBeHidden();
  await expect(surface.getByRole("button", { name: "Model Alpha" })).toBeVisible();
});
