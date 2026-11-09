import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Locator } from "@playwright/test";
import { expect, test } from "../harness/fixtures";
import {
  createWorkflowSession,
  workflowReceipt,
  workflowTool,
  writeWorkflow,
} from "../harness/workflows";

const QUESTION = "Does the review meet its acceptance criteria?";
const DESCRIPTION = "The supplied review satisfies every acceptance criterion.";
const STAGES = `  - id: review
    run:
      prompt: Review the evidence.
      outputs: { result: text }
    then:
      ask: ${QUESTION}
      by: model
      choices:
        accept: { then: accepted, description: "${DESCRIPTION}" }
        revise: review
  - id: accepted
    run:
      prompt: The review was accepted.
`;

test.use({ inference: "workflow" });

async function reply(worktree: string, probabilities: Record<string, number>): Promise<void> {
  await writeFile(join(worktree, "inference-answer.json"), JSON.stringify(probabilities));
}

async function start(surface: Locator): Promise<string> {
  const response = await workflowTool(surface, "runCommand", {
    id: "weavie.workflow.run",
    args: { workflow: "confidence", inputs: {} },
  });
  return /Started run (r-[a-f0-9]+)/.exec(response)![1]!;
}

const complete = (surface: Locator) =>
  workflowTool(surface, "completeStage", {
    outputs: { result: "Acceptance criteria checked." },
    summary: "Every acceptance criterion was reviewed.",
  });

test("confidence boundaries, live settings, described choices, and human feedback", async ({
  page,
  weavie,
}) => {
  const { surface, worktree, run } = await createWorkflowSession(
    page,
    weavie.home,
    weavie.workspace,
    "claude",
  );
  await writeWorkflow(
    worktree,
    "confidence",
    STAGES.replace("    run:", '    if: { ask: "Is the evidence ready?", by: model }\n    run:'),
  );
  await reply(worktree, { yes: 0.8, no: 0.2 });
  const id = await start(surface);
  await expect(run).toHaveText("confidence · review");
  await expect(run).toHaveAttribute("title", /review · waiting on you/);
  const condition = await workflowReceipt(worktree);
  expect(condition.choices.map((choice: { label: string }) => choice.label)).toEqual(["yes", "no"]);

  await reply(worktree, { accept: 0.79, revise: 0.21 });
  await complete(surface);
  await expect(run).toHaveAttribute("title", /confidence 0\.79 is below the required 0\.8/);
  await expect(
    surface.locator(".agent-entry-message.agent-tone-assistant", {
      hasText: "Weavie now asks the user:",
    }),
  ).toContainText(`${QUESTION} (answers: accept, revise)`);
  const input = await workflowReceipt(worktree);
  expect(Object.keys(input).sort()).toEqual(["choices", "evidence", "question"]);
  expect(input).toEqual({
    question: QUESTION,
    choices: [
      { label: "accept", description: DESCRIPTION },
      { label: "revise", description: null },
    ],
    evidence: {
      outputs: { result: "Acceptance criteria checked." },
      summary: "Every acceptance criterion was reviewed.",
      files: [],
    },
  });

  await workflowTool(surface, "runCommand", {
    id: "weavie.workflow.answer",
    args: { run: id, choice: "revise", feedback: "Keep the concrete evidence in the review." },
  });
  await expect(run).toHaveAttribute("title", /review · waiting on you/);
  await expect(
    surface.locator(".agent-entry-message.agent-tone-assistant", {
      hasText: "Keep the concrete evidence in the review.",
    }),
  ).toContainText("Revise the work accordingly");
  await workflowTool(surface, "setSetting", { key: "workflow.decisionConfidence", value: 0.79 });
  await reply(worktree, { accept: 0.79, revise: 0.21 });
  await complete(surface);
  await expect(run).toHaveText("confidence · accepted");
  await expect(surface.locator(".agent-entry-message.agent-tone-assistant").last()).toContainText(
    "The review was accepted.",
  );
});

test("if and then confidence overrides take precedence over the live setting", async ({
  page,
  weavie,
}) => {
  const { surface, worktree, run } = await createWorkflowSession(
    page,
    weavie.home,
    weavie.workspace,
    "workflow-acp",
  );
  await writeWorkflow(
    worktree,
    "confidence",
    STAGES.replace(
      "    run:",
      '    if: { ask: "Is the evidence ready?", by: model, confidence: 0.7 }\n    run:',
    ).replace("      by: model", "      by: model\n      confidence: 0.9"),
  );
  await reply(worktree, { yes: 0.7, no: 0.3 });
  const id = await start(surface);
  await expect(run).toHaveAttribute("title", /review · waiting on you/);
  await workflowTool(surface, "setSetting", { key: "workflow.decisionConfidence", value: 0.5 });
  await reply(worktree, { accept: 0.85, revise: 0.15 });
  await complete(surface);
  await expect(run).toHaveAttribute("title", /confidence 0\.85 is below the required 0\.9/);
  const input = await workflowReceipt(worktree);
  expect(Object.keys(input).sort()).toEqual(["choices", "evidence", "question"]);
  expect(input.choices[0]).toEqual({ label: "accept", description: DESCRIPTION });
  await workflowTool(surface, "runCommand", {
    id: "weavie.workflow.answer",
    args: { run: id, choice: "accept", feedback: "I checked the remaining criteria." },
  });
  await expect(run).toHaveText("confidence · accepted");
  await expect(surface.locator(".agent-entry-message.agent-tone-assistant").last()).toContainText(
    "I checked the remaining criteria.",
  );
});

for (const scenario of [
  { name: "refusal", provider: "workflow-acp", answer: null, reason: /decision model refused/ },
  {
    name: "missing label",
    provider: "claude",
    answer: '{"accept":1}',
    reason: /InvalidResponse: The provider returned no probability for 'revise'/,
  },
  { name: "malformed JSON", provider: "claude", answer: '{"accept":', reason: /InvalidResponse/ },
  {
    name: "unknown label",
    provider: "workflow-acp",
    answer: '{"accept":0.1,"revise":0,"unknown":0.9}',
    reason: /InvalidResponse: The provider returned unknown label 'unknown'/,
  },
  {
    name: "out-of-range probability",
    provider: "workflow-acp",
    answer: '{"accept":1.2,"revise":0}',
    reason: /InvalidResponse: The provider returned a probability outside \[0, 1\]/,
  },
  {
    name: "non-numeric probability",
    provider: "claude",
    answer: '{"accept":"high","revise":0}',
    reason:
      /InvalidResponse: The provider returned JSON that does not match the declared response type/,
  },
]) {
  test(`${scenario.name} escalates with the same question and choices`, async ({
    page,
    weavie,
  }) => {
    const { surface, worktree, run } = await createWorkflowSession(
      page,
      weavie.home,
      weavie.workspace,
      scenario.provider,
    );
    await writeWorkflow(worktree, "confidence", STAGES);
    if (scenario.answer === null) {
      await writeFile(join(worktree, "inference-refuse"), "");
    } else {
      await writeFile(join(worktree, "inference-answer.json"), scenario.answer);
    }
    await start(surface);
    await expect(run).toHaveAttribute("title", /review · waiting on you/);
    await complete(surface);
    await expect(run).toHaveAttribute("title", /review · needs your answer/);
    await expect(run).toHaveAttribute("title", scenario.reason);
    await expect(
      surface.locator(".agent-entry-message.agent-tone-assistant", {
        hasText: "Weavie now asks the user:",
      }),
    ).toContainText(`${QUESTION} (answers: accept, revise)`);
  });
}
