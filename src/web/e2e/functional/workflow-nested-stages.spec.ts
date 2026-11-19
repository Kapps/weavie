import type { Locator, Page } from "@playwright/test";
import { createAcpSession } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";
import { answerWorkflow, workflowTool, writeWorkflowSource } from "../harness/workflows";

const GROUPS = `id: groups
description: Each group in its own conversation, each task forking its group.
stages:
  - id: list
    run: { prompt: List the groups }
    outputs: { groups: list }
  - id: group
    foreach: "{{list.groups}}"
    context: fresh
    outputs: { note: "{{task.note}}" }
    stages:
      - id: plan
        run: { prompt: "Plan {{group.item}}" }
      - id: task
        context: fork
        run: { prompt: "Build {{group.item}}" }
        outputs: { note: text }
        then:
          ask: "Ship {{task.note}}?"
          by: user
          choices: { ship: done, redo: task }
  - id: publish
    run: { prompt: "Publish {{group.note}}" }
`;

// The card whose title is exactly `title`.
const card = (page: Page, surface: Locator, title: string) =>
  surface.locator(".agent-aside").filter({
    has: page.locator(".agent-aside-toggle-label", { hasText: new RegExp(`^${title}$`) }),
  });

async function reply(card: Locator, text: string): Promise<void> {
  const box = card.getByRole("textbox", { name: "Reply to BTW" });
  if (!(await box.isVisible()))
    await card.getByRole("button", { name: "Reply", exact: true }).click();
  await box.fill(text);
  await box.press("Enter");
}

const complete = (outputs: unknown) =>
  `mcp:completeStage:${JSON.stringify({ outputs, summary: "Finished the stage" })}`;

test("a repeated group runs each entry in its own conversation and asks its questions in main", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "workflow-nested");
  await writeWorkflowSource(sessionWorktrees(weavie.workspace)[0]!, "groups", GROUPS);
  await workflowTool(surface, "runCommand", {
    id: "weavie.workflow.run",
    args: { workflow: "groups", inputs: {} },
  });
  const run = surface.locator(".pane-head .pane-run");
  const mainRows = surface.locator(".agent-transcript > .agent-virtual-row").filter({
    has: page.locator(".agent-entry-message.agent-tone-assistant"),
    hasNot: page.locator(".agent-aside"),
  });
  await workflowTool(surface, "completeStage", {
    outputs: { groups: ["auth", "billing"] },
    summary: "Listed the groups",
  });

  for (const [index, item] of ["auth", "billing"].entries()) {
    const group = card(page, surface, `group ${index + 1}`);
    await expect(group).toContainText(`echo: Plan ${item}`);
    await expect(run).toHaveText(`groups · group ${index + 1}/2 › plan`);
    await reply(group, complete({}));

    const task = card(page, surface, `group ${index + 1} › task`);
    await expect(task).toContainText(`echo: Build ${item}`);
    await expect(run).toHaveText(`groups · group ${index + 1}/2 › task`);
    await reply(task, complete({ note: `${item} built` }));

    await expect(
      mainRows.filter({ hasText: `Weavie now asks the user: Ship ${item} built?` }),
    ).toHaveCount(1);
    await expect(
      mainRows.filter({
        hasText: new RegExp(`inside .group. [(]entry ${index + 1} of 2: .${item}.[)]`),
      }),
    ).not.toHaveCount(0);
    await answerWorkflow(surface, weavie.home, "ship", "");
    await expect(task.getByRole("button", { name: "Reply", exact: true })).toHaveCount(0);
    await expect(group.getByRole("button", { name: "Reply", exact: true })).toHaveCount(0);
  }

  await expect(mainRows.filter({ hasText: "Publish - auth: auth built" })).toHaveCount(1);
  await expect(mainRows.last()).toContainText("billing: billing built");
  await expect(run).toHaveText("groups · publish");
});
