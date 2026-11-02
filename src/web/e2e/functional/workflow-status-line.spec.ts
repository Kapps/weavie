import { existsSync } from "node:fs";
import { mkdir, readdir, readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

// A session running a workflow shows its stage and run state in the session list's status line, and a stage
// awaiting approval moves the session under "Needs you". See docs/specs/workflows.md.

const TWO_STEP = `id: two-step
description: Hold in a gated stage, then ship.
stages:
  - id: implement
    do:
      prompt: hold
    gate: approve
  - id: ship
    do:
      prompt: ship it
`;

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

test("the session line follows the run through a gate to done", async ({ page, weavie }) => {
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
  await expect(chip).not.toHaveAttribute("title", /approval needs you/);

  await writeFile(join(worktree, "release-hold"), "");
  await expect(chip).toHaveAttribute("title", line(branch, "implement · waiting on you"));

  await submitAcpDraft(surface, tool("completeStage", { outputs: {} }));
  await expect(chip).toHaveAttribute("title", line(branch, "implement · approval needs you"));
  await runCommand(page, "Pin Session Panel");
  await expect(page.locator(".session-panel-group").first()).toHaveText("Needs you");
  await expect(page.locator(".session-panel-row").first()).toContainText(
    "implement · approval needs you",
  );
  await runCommand(page, "Pin Session Panel");

  await submitAcpDraft(
    surface,
    tool("runCommand", {
      id: "weavie.workflow.approveStage",
      args: { run: await runId(weavie.home) },
    }),
  );
  await expect(chip).toHaveAttribute("title", line(branch, "ship · waiting on you"));

  await submitAcpDraft(surface, tool("completeStage", { outputs: {} }));
  await expect(chip).toHaveAttribute("title", line(branch, "idle · .+"));
});
