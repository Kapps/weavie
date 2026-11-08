import { existsSync } from "node:fs";
import { readFile, unlink, writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Locator } from "@playwright/test";
import { clickIntoEditor, openFile, typeInEditor } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import {
  createWorkflowSession,
  workflowReceipt,
  workflowTool,
  writeWorkflow,
  writeWorkflowSource,
} from "../harness/workflows";

const QUESTION = "Do the supplied documents satisfy the acceptance criteria?";
const SUMMARY = "Reviewed the supplied documents.";
const REVIEW = `  - id: review
    run:
      prompt: Review the documents.
      outputs: { documents: files, ignored: files, directory: text }
    then:
      ask: "${QUESTION} {{review.documents}}"
      choices: { accept: accepted, revise: review }
  - id: accepted
    run: { prompt: The documents were accepted. }
`;

test.use({ inference: "workflow" });

async function answer(worktree: string, choice: string): Promise<void> {
  await writeFile(
    join(worktree, "inference-answer.json"),
    JSON.stringify({ choice, confidence: 1 }),
  );
}

async function start(surface: Locator, inputs: Record<string, unknown>): Promise<string> {
  const response = await workflowTool(surface, "runCommand", {
    id: "weavie.workflow.run",
    args: { workflow: "file-evidence", inputs },
  });
  return /Started run (r-[a-f0-9]+)/.exec(response)![1]!;
}

const complete = (surface: Locator, outputs: Record<string, unknown>) =>
  workflowTool(surface, "completeStage", { outputs, summary: SUMMARY });

for (const provider of ["claude", "workflow-acp"]) {
  test(`${provider} receives current, earlier, and input file text once and rereads after editing`, async ({
    page,
    weavie,
  }) => {
    const { surface, worktree, run } = await createWorkflowSession(
      page,
      weavie.home,
      weavie.workspace,
      provider,
    );
    const original = "# Review\nMissing criterion: release notes.\n";
    const revised = "# Review\nAll criteria satisfied, including release notes.\n";
    await writeFile(join(worktree, "input.mp4"), "Acceptance criteria: include release notes.\n");
    await writeFile(join(worktree, "earlier.md"), "Previous review: release notes required.\n");
    await writeFile(join(worktree, "review.md"), original);
    await writeFile(join(worktree, "ignored.mp4"), Buffer.from([0, 255, 1]));
    await writeWorkflowSource(
      worktree,
      "file-evidence",
      `id: file-evidence
description: Judge explicit file evidence.
inputs: { source: files }
stages:
  - id: draft
    run: { prompt: Gather the documents., outputs: { documents: files } }
${REVIEW.replace(
  "    run:",
  '    if: "Are {{inputs.source}} and {{draft.documents}} ready?"\n    run:',
).replace(
  "{{review.documents}}",
  "{{review.documents}} {{draft.documents}} {{inputs.source}} {{review.documents}}",
)}`,
    );
    await answer(worktree, "yes");
    await start(surface, { source: ["input.mp4", "./input.mp4"] });
    await expect(run).toHaveAttribute("title", /draft · waiting on you/);
    await complete(surface, { documents: ["earlier.md"] });
    await expect(run).toHaveAttribute("title", /review · waiting on you/);
    const condition = await workflowReceipt(worktree);
    expect(condition.evidence.files).toEqual([
      { path: "input.mp4", text: "Acceptance criteria: include release notes.\n" },
      { path: "earlier.md", text: "Previous review: release notes required.\n" },
    ]);

    const outputs = {
      documents: ["review.md", "./review.md"],
      ignored: ["ignored.mp4"],
      directory: ".",
    };
    await answer(worktree, "revise");
    await complete(surface, outputs);
    await expect(run).toHaveAttribute("title", /review · waiting on you/);
    await expect(surface.locator(".agent-entry-message.agent-tone-assistant").last()).toContainText(
      "Revise the work accordingly",
    );
    const first = await workflowReceipt(worktree);
    expect(first.evidence.files).toEqual([
      { path: "review.md", text: original },
      { path: "earlier.md", text: "Previous review: release notes required.\n" },
      { path: "input.mp4", text: "Acceptance criteria: include release notes.\n" },
    ]);
    expect(first.question).toContain("- review.md\n- ./review.md");
    expect(first.question).not.toContain(original);
    expect(first.evidence.outputs).toEqual({
      documents: "- review.md\n- ./review.md",
      ignored: "- ignored.mp4",
      directory: ".",
    });
    expect(first.evidence.summary).toBe(SUMMARY);

    await openFile(page, "review.md");
    await clickIntoEditor(page);
    await page.keyboard.press("ControlOrMeta+a");
    await typeInEditor(page, revised);
    await page.keyboard.press("ControlOrMeta+s");
    await expect.poll(() => readFile(join(worktree, "review.md"), "utf8")).toBe(revised);
    await answer(worktree, "accept");
    await complete(surface, outputs);
    await expect(run).toHaveText("file-evidence · accepted");
    const second = await workflowReceipt(worktree);
    expect(second.evidence.files).toEqual([
      { path: "review.md", text: revised },
      ...first.evidence.files.slice(1),
    ]);
  });
}

test("mixed readable and binary evidence asks the user without any model call", async ({
  page,
  weavie,
}) => {
  const { surface, worktree, run } = await createWorkflowSession(
    page,
    weavie.home,
    weavie.workspace,
    "claude",
  );
  await writeFile(join(worktree, "review.md"), "Readable review.");
  await writeFile(join(worktree, "clip.md"), Buffer.from([0, 255, 1]));
  await writeWorkflow(worktree, "file-evidence", REVIEW);
  const id = await start(surface, {});
  await complete(surface, { documents: ["review.md", "clip.md"], ignored: [], directory: "." });
  await expect(run).toHaveAttribute("title", /review · needs your answer/);
  await expect(run).toHaveAttribute("title", /"clip.md": not readable text/);
  expect(existsSync(join(worktree, "inference-receipt.txt"))).toBe(false);
  await expect(surface.locator(".agent-entry-message.agent-tone-assistant").last()).toContainText(
    QUESTION,
  );
  await workflowTool(surface, "runCommand", {
    id: "weavie.workflow.answer",
    args: { run: id, choice: "accept", feedback: "I inspected the binary evidence myself." },
  });
  await expect(run).toHaveText("file-evidence · accepted");
  await expect(surface.locator(".agent-entry-message.agent-tone-assistant").last()).toContainText(
    "I inspected the binary evidence myself.",
  );
  expect(existsSync(join(worktree, "inference-receipt.txt"))).toBe(false);
});

test("a file deleted after output validation escalates a later question without partial evidence", async ({
  page,
  weavie,
}) => {
  const { surface, worktree, run } = await createWorkflowSession(
    page,
    weavie.home,
    weavie.workspace,
    "workflow-acp",
  );
  await writeFile(join(worktree, "earlier.md"), "Accepted output.");
  await writeFile(join(worktree, "input.md"), "Readable input.");
  await writeWorkflowSource(
    worktree,
    "file-evidence",
    `id: file-evidence
description: File deleted after validation.
inputs: { source: files }
stages:
  - id: draft
    run: { prompt: Gather the documents., outputs: { documents: files } }
  - id: edit
    run: { prompt: Edit the documents. }
  - id: review
    if: "Are {{inputs.source}} and {{draft.documents}} complete?"
    run: { prompt: The documents were accepted. }
`,
  );
  const id = await start(surface, { source: ["input.md"] });
  await complete(surface, { documents: ["earlier.md"] });
  await expect(run).toHaveAttribute("title", /edit · waiting on you/);
  await unlink(join(worktree, "earlier.md"));
  await complete(surface, {});
  await expect(run).toHaveAttribute("title", /review · needs your answer/);
  await expect(run).toHaveAttribute("title", /"earlier.md": file no longer exists/);
  expect(existsSync(join(worktree, "inference-receipt.txt"))).toBe(false);
  await workflowTool(surface, "runCommand", {
    id: "weavie.workflow.answer",
    args: { run: id, choice: "yes", feedback: "Proceed with my review." },
  });
  await expect(run).toHaveAttribute("title", /review · waiting on you/);
});

test("literal paths, composed paths, unreferenced outputs, and document links add no file evidence", async ({
  page,
  weavie,
}) => {
  const { surface, worktree, run } = await createWorkflowSession(
    page,
    weavie.home,
    weavie.workspace,
    "claude",
  );
  await writeFile(join(worktree, "ignored.mp4"), Buffer.from([0, 255, 1]));
  const references =
    "Review missing.md and {{review.directory}}/missing.md and https://example.com/doc?";
  await writeWorkflow(
    worktree,
    "file-evidence",
    REVIEW.replace(`${QUESTION} {{review.documents}}`, references),
  );
  const id = await start(surface, {});
  await complete(surface, { documents: ["ignored.mp4"], ignored: [], directory: "docs" });
  await expect(run).toHaveText("file-evidence · accepted");
  expect((await workflowReceipt(worktree)).evidence.files).toEqual([]);

  await workflowTool(surface, "runCommand", { id: "weavie.workflow.cancel", args: { run: id } });
  await writeFile(
    join(worktree, "review.md"),
    "See [missing](missing.md) and https://example.com/doc.\n",
  );
  await writeWorkflow(worktree, "file-evidence", REVIEW);
  await start(surface, {});
  await complete(surface, {
    documents: ["review.md"],
    ignored: ["ignored.mp4"],
    directory: "docs",
  });
  await expect(run).toHaveText("file-evidence · accepted");
  expect((await workflowReceipt(worktree)).evidence.files).toEqual([
    { path: "review.md", text: "See [missing](missing.md) and https://example.com/doc.\n" },
  ]);
});
