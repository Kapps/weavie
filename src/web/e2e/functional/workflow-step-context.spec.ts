import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";
import { workflowTool, writeWorkflowSource } from "../harness/workflows";

test("a fresh workflow child keeps revisions while main receives its report and next stage", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "workflow-child");
  await writeWorkflowSource(
    sessionWorktrees(weavie.workspace)[0]!,
    "review-child",
    `id: review-child
description: Review in a separate conversation.
stages:
  - id: review
    run: { context: fresh, prompt: Review cancellation, outputs: { note: text } }
    then:
      ask: "Accept {{review.note}}?"
      by: user
      choices: { revise: review, approve: publish }
  - id: publish
    run: { prompt: "Publish after {{review.note}}" }
`,
  );
  const started = await workflowTool(surface, "runCommand", {
    id: "weavie.workflow.run",
    args: { workflow: "review-child", inputs: {} },
  });
  const run = /r-[0-9a-f]+/.exec(started)![0];
  const child = surface.locator(".agent-aside");
  const mainRows = surface.locator(".agent-transcript > .agent-virtual-row").filter({
    has: page.locator(".agent-entry-message.agent-tone-assistant"),
    hasNot: page.locator(".agent-aside"),
  });
  await expect(child).toContainText("echo: Review cancellation");
  await expect(child.locator(".agent-aside-head")).toContainText("review");
  const identity = await child.getAttribute("data-agent-aside");
  await submitAcpDraft(surface, "main remains usable");
  await expect(mainRows.filter({ hasText: "echo: main remains usable" })).toHaveCount(1);

  const complete = async () => {
    const reply = child.getByRole("textbox", { name: "Reply to BTW" });
    if (!(await reply.isVisible())) {
      await child.getByRole("button", { name: "Reply", exact: true }).click();
    }
    await reply.fill(
      `mcp:completeStage:${JSON.stringify({
        outputs: { note: "cancellation checked" },
        summary: "No blocking findings",
      })}`,
    );
    await reply.press("Enter");
  };
  const scrollToChild = async () => {
    await surface.locator(".agent-transcript-scroller").hover();
    await page.mouse.wheel(0, -10000);
  };
  const followMain = async () => {
    await surface.locator("[data-agent-composer] textarea").click();
    await runCommand(page, "Jump to Latest Agent Activity");
  };

  await complete();
  await followMain();
  await expect(mainRows.filter({ hasText: "Weavie now asks the user:" })).toContainText(
    "Accept cancellation checked?",
  );
  await expect(mainRows.filter({ hasText: "Summary: No blocking findings" })).toHaveCount(1);
  await workflowTool(surface, "runCommand", {
    id: "weavie.workflow.answer",
    args: { run, choice: "revise", feedback: "Include shutdown" },
  });
  await scrollToChild();
  await expect(child).toHaveAttribute("data-agent-aside", identity!);
  await expect(child).toContainText("Include shutdown");
  await complete();
  await expect(surface.locator(".pane-head .pane-run")).toHaveAttribute(
    "title",
    /needs your answer/,
  );
  await followMain();
  await workflowTool(surface, "runCommand", {
    id: "weavie.workflow.answer",
    args: { run, choice: "approve", feedback: "" },
  });
  await expect(
    mainRows.filter({ hasText: "echo: Publish after cancellation checked" }),
  ).toHaveCount(1);
  await scrollToChild();
  await expect(child).toHaveAttribute("data-agent-aside", identity!);
  await expect(child.getByRole("button", { name: "Reply", exact: true })).toHaveCount(0);
  await expect(child.getByRole("textbox")).toHaveCount(0);
  await followMain();
  await workflowTool(surface, "completeStage", { outputs: {}, summary: "Published after review" });
  await expect(surface.locator(".pane-head .pane-run")).toBeHidden();
});
