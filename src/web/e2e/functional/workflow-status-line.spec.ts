import { existsSync } from "node:fs";
import { mkdir, readdir, readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Locator, Page } from "@playwright/test";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { activeSessionSlot, createSession, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

// A session running a workflow shows its stage and run state in the session list's status line and its pane header,
// and a question awaiting the user's answer moves the session under "Needs you". See docs/specs/workflows.md.

const APPROVAL_QUESTION = "Ship it after reviewing {{implement.videos}}?";

const TWO_STEP = `id: two-step
description: Hold in a stage the user approves, then ship.
stages:
  - id: implement
    run:
      prompt: completed-tool-then-hold
      outputs: { videos: list }
    then:
      ask: "${APPROVAL_QUESTION}"
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

// Writes TWO_STEP into `dir`'s workflows under `workflow`, its id and file name.
async function writeWorkflow(dir: string, workflow: string): Promise<void> {
  await mkdir(join(dir, ".weavie", "workflows"), { recursive: true });
  await writeFile(
    join(dir, ".weavie", "workflows", `${workflow}.yaml`),
    TWO_STEP.replace("id: two-step", `id: ${workflow}`),
  );
}

// Starts TWO_STEP and holds its live turn after a command completes, before the prompt returns.
async function startTwoStep(page: Page, workspace: string, branch: string, workflow: string) {
  const surface = await createAcpSession(page, branch);
  const worktree = sessionWorktrees(workspace)[0]!;
  await writeWorkflow(worktree, workflow);
  await writeFile(join(worktree, "hold-after-tool"), "");
  await submitAcpDraft(
    surface,
    tool("runCommand", { id: "weavie.workflow.run", args: { workflow, inputs: {} } }),
  );
  await expect.poll(() => existsSync(join(worktree, "after-tool-started"))).toBe(true);
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
  const { surface, worktree } = await startTwoStep(page, weavie.workspace, branch, "two-step");
  const chip = page.locator(".session-chip.active");

  await expect(chip).toHaveAttribute("title", new RegExp(`^${branch} — implement · `));
  await expect(chip).not.toHaveAttribute("title", /needs your answer/);

  await writeFile(join(worktree, "release-after-tool"), "");
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

test("the pane header shows the session's own run until it ends", async ({ page, weavie }) => {
  const branch = "workflow-header";
  const { surface, worktree } = await startTwoStep(page, weavie.workspace, branch, "two-step");
  const run = surface.locator(".pane-head .pane-run");
  const dot = run.locator(".session-status");
  const sessionDot = page.locator(".session-chip.active .session-status");
  const footerDot = page.locator('.terminal-surface[data-kind="terminal:shell"] .session-status');

  await expect(run).toHaveText("two-step · implement");
  await expect(run).toHaveAttribute("title", "two-step · implement · working");
  await expect(dot).toHaveCSS("background-color", await tokenColor(page, "--busy"));
  await expect(sessionDot).toHaveCSS("background-color", await tokenColor(page, "--busy"));
  await expect(footerDot).toHaveCSS("background-color", await tokenColor(page, "--busy"));
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

  await writeFile(join(worktree, "release-after-tool"), "");
  await expect(run).toHaveAttribute("title", "two-step · implement · waiting on you");
  await expect(dot).toHaveCSS("background-color", await tokenColor(page, "--warn"));
  await expect(sessionDot).toHaveCSS("background-color", await tokenColor(page, "--warn"));
  await expect(footerDot).toHaveCSS("background-color", await tokenColor(page, "--warn"));
  await expect(footerDot.locator("..")).toHaveText("Needs input");

  await submitAcpDraft(
    surface,
    tool("completeStage", { outputs: { videos: VIDEOS }, summary: "held" }),
  );
  const question = APPROVAL_QUESTION.replace(
    "{{implement.videos}}",
    VIDEOS.map((video) => `- ${video}`).join("\n"),
  );
  await expect(run).toHaveAttribute(
    "title",
    `two-step · implement · needs your answer\n${question}`,
  );
  await expect(dot).toHaveCSS("background-color", await tokenColor(page, "--warn"));
  await expect(sessionDot).toHaveCSS("background-color", await tokenColor(page, "--warn"));
  await expect(footerDot).toHaveCSS("background-color", await tokenColor(page, "--warn"));
  await expect(footerDot.locator("..")).toHaveText("Needs input");
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
  await expect(sessionDot).toHaveCSS("background-color", await tokenColor(page, "--dim"));
});

// Whether `locator`'s text is cut off, which its ellipsis then marks.
const truncated = (locator: Locator): Promise<boolean> =>
  locator.evaluate((element) => element.scrollWidth > element.clientWidth);

test("the header run line keeps its place and its stage as space runs out", async ({
  page,
  weavie,
}) => {
  const workflow = "a-workflow-whose-name-is-long-enough-to-crowd-the-agent-pane-header";
  const { surface } = await startTwoStep(page, weavie.workspace, "workflow-header-fit", workflow);
  const head = surface.locator(".pane-head");
  const label = head.locator(".pane-label");
  const run = head.locator(".pane-run");
  const fullscreen = head.locator(".pane-fullscreen");
  const badge = head.locator(".pane-shortcut");
  const box = async (locator: Locator) => (await locator.boundingBox())!;
  const right = async (locator: Locator) => (await box(locator)).x + (await box(locator)).width;
  const style = (property: "columnGap" | "paddingRight") =>
    head.evaluate((element, name) => parseFloat(getComputedStyle(element)[name]), property);
  const contentEdge = async () => (await right(head)) - (await style("paddingRight"));
  await expect(run).toHaveText(`${workflow} · implement`);

  await expect(badge).toBeVisible();
  // Wider than the line needs, so free space would split between two auto margins.
  await head.evaluate((element) => {
    element.style.width = "2000px";
  });
  const gap = await style("columnGap");
  expect(Math.abs((await box(fullscreen)).x - (await right(run)) - gap)).toBeLessThan(2);
  expect(Math.abs((await box(badge)).x - (await right(fullscreen)) - gap)).toBeLessThan(2);
  expect(Math.abs((await right(badge)) - (await contentEdge()))).toBeLessThan(2);

  const labelBox = await box(label);
  for (const width of ["320px", "150px"]) {
    await head.evaluate((element, value) => {
      element.style.width = value;
    }, width);
    await expect.poll(() => truncated(run.locator(".pane-run-workflow"))).toBe(true);
    expect(await truncated(run.locator(".pane-run-stage"))).toBe(width === "150px");
    expect(await box(label)).toMatchObject({ width: labelBox.width, height: labelBox.height });
  }
  await head.evaluate((element) => {
    element.style.width = "";
  });

  await writeFile(
    join(weavie.home, ".weavie", "keybindings.json"),
    JSON.stringify(
      [1, 2, 3, 4, 5, 6, 7, 8, 9].map((index) => ({
        key: `ctrl+${index}`,
        command: "-weavie.pane.focusByIndex",
        args: { index },
      })),
    ),
  );
  await expect(badge).toHaveCount(0);
  expect(Math.abs((await right(fullscreen)) - (await contentEdge()))).toBeLessThan(2);

  await page.setViewportSize({ width: 700, height: 800 });
  await expect(page.locator(".app.compact")).toHaveCount(1);
  await page.getByRole("button", { name: "Agent", exact: true }).click();
  await expect(head).toBeVisible();
  await expect(run).toHaveCSS("display", "none");
  await expect(label).toBeVisible();
});

test.describe("terminal Claude", () => {
  // The workspace's own terminal Claude starts TWO_STEP once signalled; a new session waits forever.
  test.use({
    fakeScript: {
      steps: [
        { op: "waitFile", path: "start-two-step" },
        {
          op: "mcp",
          tool: "runCommand",
          args: { id: "weavie.workflow.run", args: { workflow: "two-step", inputs: {} } },
        },
      ],
    },
  });

  test("the terminal header shows the selected session's run", async ({ page, weavie }) => {
    const run = page.locator(".agent-terminal-surface .pane-head .pane-run");
    const mainSlot = await activeSessionSlot(page);
    await writeWorkflow(weavie.workspace, "two-step");
    await writeFile(join(weavie.workspace, "start-two-step"), "");
    await expect(run).toHaveText("two-step · implement");

    await createSession(page, { branch: "workflow-terminal-other", provider: "claude" });
    await expect(page.locator(".agent-terminal-surface")).toBeVisible();
    await expect(run).toHaveCount(0);

    await page.locator(`.session-chip[data-session-slot="${mainSlot}"]`).click();
    await expect(run).toHaveText("two-step · implement");
  });
});
