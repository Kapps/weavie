import { existsSync } from "node:fs";
import { mkdir, readdir, readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

// A session running a workflow shows its stage and run state in the session list's status line, and a question
// awaiting the user's answer moves the session under "Needs you". See docs/specs/workflows.md.

const TWO_STEP = `id: two-step
description: Hold in a stage the user approves, then ship.
stages:
  - id: implement
    run:
      prompt: hold
      outputs: { videos: list }
    then:
      ask: "Ship it after reviewing {{implement.videos}}?"
      by: user
      choices:
        approve: ship
        revise: implement
  - id: ship
    run:
      prompt: ship it
`;

const VIDEOS = Array.from(
  { length: 12 },
  (_, index) => `src/web/e2e/.recordings/workflow-header-group-${index + 1}-journey.mp4`,
);

// The chip's title is `<label> — <status line>`, followed by its switch shortcut when it has one.
const line = (branch: string, text: string): RegExp => new RegExp(`^${branch} — ${text}(?: \\(|$)`);

const tool = (name: string, args: unknown): string => `mcp:${name}:${JSON.stringify(args)}`;

async function runId(home: string): Promise<string> {
  const workspaces = join(home, ".weavie", "workspaces");
  for (const dir of await readdir(workspaces)) {
    const file = join(workspaces, dir, "workflow-run.json");
    const id = existsSync(file) ? /"(r-[0-9a-f]+)"/.exec(await readFile(file, "utf8")) : null;
    if (id !== null) {
      return id[1]!;
    }
  }
  throw new Error("No workflow run was persisted.");
}

test("the session line follows the run through a user's answer to done", async ({
  page,
  weavie,
}) => {
  const branch = "workflow-status-line";
  const surface = await createAcpSession(page, branch);
  const worktree = sessionWorktrees(weavie.workspace)[0]!;
  await mkdir(join(worktree, ".weavie", "workflows"), { recursive: true });
  await writeFile(join(worktree, ".weavie", "workflows", "two-step.yaml"), TWO_STEP);
  const chip = page.locator(".session-chip.active");

  await submitAcpDraft(
    surface,
    tool("runCommand", { id: "weavie.workflow.run", args: { workflow: "two-step", inputs: {} } }),
  );
  await expect(chip).toHaveAttribute("title", new RegExp(`^${branch} — implement · `));
  await expect(chip).not.toHaveAttribute("title", /needs your answer/);

  await writeFile(join(worktree, "release-hold"), "");
  await expect(chip).toHaveAttribute("title", line(branch, "implement · waiting on you"));

  await submitAcpDraft(
    surface,
    tool("completeStage", { outputs: { videos: VIDEOS }, summary: "held" }),
  );
  await expect(chip).toHaveAttribute("title", line(branch, "implement · needs your answer"));
  const question = surface.locator(".agent-entry-message.agent-tone-assistant", {
    hasText: "Weavie now asks the user:",
  });
  await expect(question).toContainText(VIDEOS[0]!);
  await expect(question).toContainText(VIDEOS.at(-1)!);
  await expect(question).toContainText("answers: approve, revise");
  await expect(page.locator(".toast-warn")).toHaveCount(0);
  await runCommand(page, "Pin Session Panel");
  await expect(page.locator(".session-panel-group").first()).toHaveText("Needs you");
  await expect(page.locator(".session-panel-row").first()).toContainText(
    "implement · needs your answer",
  );
  await runCommand(page, "Pin Session Panel");

  await submitAcpDraft(
    surface,
    tool("runCommand", {
      id: "weavie.workflow.answer",
      args: { run: await runId(weavie.home), choice: "approve" },
    }),
  );
  await expect(chip).toHaveAttribute("title", line(branch, "ship · waiting on you"));

  await submitAcpDraft(surface, tool("completeStage", { outputs: {}, summary: "shipped" }));
  await expect(chip).toHaveAttribute("title", line(branch, "idle · .+"));
});
