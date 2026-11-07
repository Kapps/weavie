import { existsSync } from "node:fs";
import { mkdir, readdir, readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

// A session running a workflow shows its stage and run state in the session list's status line and its pane header,
// and a question awaiting the user's answer moves the session under "Needs you". See docs/specs/workflows.md.

const TWO_STEP = `id: two-step
description: Hold in a stage the user approves, then ship.
stages:
  - id: implement
    run:
      prompt: hold
    then:
      ask: Ship it?
      by: user
      choices:
        approve: ship
        revise: implement
  - id: ship
    run:
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

async function startTwoStep(page: Page, workspace: string, branch: string) {
  const surface = await createAcpSession(page, branch);
  const worktree = sessionWorktrees(workspace)[0]!;
  await mkdir(join(worktree, ".weavie", "workflows"), { recursive: true });
  await writeFile(join(worktree, ".weavie", "workflows", "two-step.yaml"), TWO_STEP);
  await submitAcpDraft(
    surface,
    tool("runCommand", { id: "weavie.workflow.run", args: { workflow: "two-step", inputs: {} } }),
  );
  await expect.poll(() => existsSync(join(worktree, "hold-started"))).toBe(true);
  return { surface, worktree };
}

// A theme token as the browser resolves it, to compare against computed colors.
const tokenColor = (page: Page, token: string): Promise<string> =>
  page.evaluate((name) => {
    const probe = document.body.appendChild(document.createElement("i"));
    probe.style.color = `var(${name})`;
    const color = getComputedStyle(probe).color;
    probe.remove();
    return color;
  }, token);

test("the session line follows the run through a user's answer to done", async ({
  page,
  weavie,
}) => {
  const branch = "workflow-status-line";
  const { surface, worktree } = await startTwoStep(page, weavie.workspace, branch);
  const chip = page.locator(".session-chip.active");

  await expect(chip).toHaveAttribute("title", new RegExp(`^${branch} — implement · `));
  await expect(chip).not.toHaveAttribute("title", /needs your answer/);

  await writeFile(join(worktree, "release-hold"), "");
  await expect(chip).toHaveAttribute("title", line(branch, "implement · waiting on you"));

  await submitAcpDraft(surface, tool("completeStage", { outputs: {}, summary: "held" }));
  await expect(chip).toHaveAttribute("title", line(branch, "implement · needs your answer"));
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

test("the pane header shows the session's own run until it ends", async ({ page, weavie }) => {
  const branch = "workflow-header";
  const { surface, worktree } = await startTwoStep(page, weavie.workspace, branch);
  const run = surface.locator(".pane-head .pane-run");
  const dot = run.locator(".session-status");

  await expect(run).toHaveText("two-step · implement");
  await expect(run).toHaveAttribute("title", "two-step · implement · working");
  await expect(dot).toHaveCSS("background-color", await tokenColor(page, "--busy"));
  await expect(dot).toHaveCSS("animation-name", "none");
  await expect(surface).toHaveClass(/\bactive\b/);
  const label = surface.locator(".pane-head .pane-label");
  for (const property of ["font-family", "font-size"]) {
    const value = await label.evaluate(
      (element, name) => getComputedStyle(element).getPropertyValue(name),
      property,
    );
    await expect(run).toHaveCSS(property, value);
  }
  await expect(run).toHaveCSS("font-weight", "400");
  await expect(run).toHaveCSS("color", await tokenColor(page, "--dim"));

  await writeFile(join(worktree, "release-hold"), "");
  await expect(run).toHaveAttribute("title", "two-step · implement · waiting on you");
  await expect(dot).toHaveCSS("background-color", await tokenColor(page, "--ok"));

  await submitAcpDraft(surface, tool("completeStage", { outputs: {}, summary: "held" }));
  await expect(run).toHaveAttribute("title", "two-step · implement · needs your answer\nShip it?");
  await expect(dot).toHaveCSS("background-color", await tokenColor(page, "--warn"));
  await expect(dot).toHaveCSS("box-shadow", "none");

  await createAcpSession(page, "workflow-header-other");
  await expect(run).toHaveCount(0);
  await page.locator(`.session-chip[title^="${branch} —"]`).click();
  await expect(run).toHaveText("two-step · implement");

  await page.reload();
  await expect(run).toHaveText("two-step · implement");

  await submitAcpDraft(
    surface,
    tool("runCommand", {
      id: "weavie.workflow.answer",
      args: { run: await runId(weavie.home), choice: "approve" },
    }),
  );
  await expect(run).toHaveText("two-step · ship");
  await expect(run).toHaveAttribute("title", "two-step · ship · waiting on you");

  await submitAcpDraft(surface, tool("completeStage", { outputs: {}, summary: "shipped" }));
  await expect(run).toHaveCount(0);
});
