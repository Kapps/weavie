import { readdir, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { expect, test } from "../harness/fixtures";
import { PNG_B64, pastePng } from "../harness/pasted-image";
import { workflowReceipt } from "../harness/workflows";

// The new-session composer picks a workflow: suggested once from the prompt by the decision provider, the user's
// to change, and started with the prompt and its images. See docs/specs/new-session-workflow-picker/design.md.

const DRAFT =
  "the WebM video in the review pane fails to load whenever the diff is reopened twice in a row";
test.use({ inference: "workflow", automaticInference: true });

test("the composer suggests a workflow from the prompt and starts the session in it with the prompt's image", async ({
  page,
  weavie,
}) => {
  await writeFile(
    join(weavie.workspace, "inference-answer.json"),
    JSON.stringify({ no_workflow: 0.05, "quick-fix": 0.9, "spec-driven": 0.05 }),
  );
  await page.locator(".session-rail-add").click();
  const inbox = page.locator(".session-inbox");
  await inbox.getByRole("combobox", { name: "Agent provider" }).selectOption("fake-acp");
  const workflow = inbox.getByRole("button", { name: "Workflow" });
  await expect(workflow).toHaveText("No workflow");

  const prompt = inbox.getByRole("textbox", { name: "Prompt for a new session" });
  await pastePng(prompt, PNG_B64);
  await prompt.fill(DRAFT);
  await expect(workflow).toHaveText("quick-fix");
  await expect(inbox.getByRole("textbox", { name: "Branch for the new session" })).toHaveValue(
    "fix/mobile-branch-inference",
  );
  const asked = await workflowReceipt(weavie.workspace);
  expect(asked.evidence).toEqual({ prompt: DRAFT });
  expect(asked.choices.map((choice: { label: string }) => choice.label)).toEqual([
    "no_workflow",
    "quick-fix",
    "spec-driven",
  ]);

  await inbox.getByRole("button", { name: "Start", exact: true }).click();
  await expect(inbox).toBeHidden();
  const surface = page.locator('[data-surface="structured-agent"]');
  await expect(surface.locator(".pane-head .pane-run")).toHaveText("quick-fix · implement");
  const turn = surface
    .locator(".agent-entry-message.agent-tone-user")
    .filter({ hasText: "Implement the task below." });
  await expect(turn).toContainText(DRAFT);
  await expect(surface.getByRole("img", { name: "Attached image" })).toHaveCount(1);

  // The image belongs to the run: it sits in the run's documents folder, outside the worktree.
  const documents = join(weavie.home, ".weavie", "workspaces");
  const saved = (await readdir(documents, { recursive: true })).filter((path) =>
    /workflow-documents\/r-[a-f0-9]+\/prompt\/r-[a-f0-9]+\/image-1\.png$/.test(
      path.replaceAll("\\", "/"),
    ),
  );
  expect(saved).toHaveLength(1);
});
