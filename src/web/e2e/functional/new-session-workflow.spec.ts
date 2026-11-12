import { readdir, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { createAcpSession } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";
import { PNG_B64, pastePng } from "../harness/pasted-image";
import { workflowReceipt, workflowTool } from "../harness/workflows";

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
  const workflow = inbox.getByRole("button", { name: /^Workflow / });
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

test("a default an agent sets from its own session is the one the composer preselects", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "sets-default");
  await workflowTool(surface, "setSetting", { key: "workflow.default", value: "quick-fix" });

  await page.locator(".session-rail-add").click();
  const workflow = page.locator(".session-inbox").getByRole("button", { name: /^Workflow / });
  await expect(workflow).toHaveText("quick-fix");
});

test("the composer asks for each role a workflow needs, suggests a model, and saves it at Start", async ({
  page,
  weavie,
}) => {
  await writeFile(
    join(weavie.workspace, "inference-answer.json"),
    JSON.stringify({ alpha: 0.05, beta: 0.95 }),
  );
  await page.locator(".session-rail-add").click();
  const inbox = page.locator(".session-inbox");
  const provider = inbox.getByRole("combobox", { name: "Agent provider" });
  await provider.selectOption("fake-acp");
  await inbox.getByRole("textbox", { name: "Prompt for a new session" }).fill("add a picker");
  await inbox.getByRole("textbox", { name: "Branch for the new session" }).fill("roles");
  await inbox.getByRole("button", { name: /^Workflow / }).click();
  await page
    .getByRole("listbox", { name: "Workflow" })
    .getByRole("option", { name: /^spec-driven/ })
    .click();

  // Each role's pickers are the agent's own: the suggested model, then the Fast mode that model offers.
  await expect(inbox.getByText("Roles for spec-driven")).toBeVisible();
  const planner = inbox.getByRole("group", { name: "planner role" });
  const builder = inbox.getByRole("group", { name: "builder role" });
  await expect(planner.getByRole("button")).toHaveText(["ModelBeta", "Fast modeOff"]);
  await expect(builder.getByRole("button")).toHaveText(["ModelBeta", "Fast modeOff"]);
  await planner.getByRole("button", { name: /Fast mode/ }).click();
  await page
    .getByRole("listbox", { name: "Fast mode" })
    .getByRole("option", { name: "On" })
    .click();
  await expect(planner.getByRole("button", { name: /Fast mode/ })).toHaveText("Fast modeOn");
  await inbox.getByRole("button", { name: "Start", exact: true }).click();
  await expect(inbox).toBeHidden();
  const surface = page.locator('[data-surface="structured-agent"]');
  await expect(surface.locator(".pane-head .pane-run")).toHaveText("spec-driven · requirements");
  await expect(surface.getByRole("button", { name: "Model Beta" })).toBeVisible();
  await expect(
    surface
      .locator(".agent-entry-notice")
      .filter({ hasText: "Workflow: requirements runs on Beta, Fast On" }),
  ).toHaveCount(1);

  // Saved for every later start in this provider's sessions; a terminal session can't run the workflow at all.
  await page.locator(".session-rail-add").click();
  await provider.selectOption("fake-acp");
  await inbox.getByRole("button", { name: /^Workflow / }).click();
  await page
    .getByRole("listbox", { name: "Workflow" })
    .getByRole("option", { name: /^spec-driven/ })
    .click();
  await expect(inbox.getByText("Roles for spec-driven")).toHaveCount(0);
  await provider.selectOption("claude");
  await inbox.getByRole("button", { name: /^Workflow / }).click();
  await expect(
    page
      .getByRole("listbox", { name: "Workflow" })
      .getByRole("option", { name: /^spec-driven.*runs in a terminal/ }),
  ).toHaveAttribute("aria-disabled", "true");
});

test("workflow refresh reconsiders the current prompt after a suggestion or manual choice", async ({
  page,
  weavie,
}) => {
  const answerPath = join(weavie.workspace, "inference-answer.json");
  await writeFile(
    answerPath,
    JSON.stringify({ no_workflow: 0.05, "quick-fix": 0.9, "spec-driven": 0.05 }),
  );
  await page.locator(".session-rail-add").click();
  const inbox = page.locator(".session-inbox");
  await inbox.getByRole("combobox", { name: "Agent provider" }).selectOption("fake-acp");
  const workflow = inbox.getByRole("button", { name: /^Workflow / });
  const refresh = inbox.getByRole("button", { name: "Suggest a workflow again" });
  const prompt = inbox.getByRole("textbox", { name: "Prompt for a new session" });
  await expect(refresh).toBeDisabled();
  await prompt.fill(DRAFT);
  await expect(workflow).toHaveText("quick-fix");

  await workflow.click();
  await page
    .getByRole("listbox", { name: "Workflow" })
    .getByRole("option", { name: /^No workflow/ })
    .click();
  await prompt.fill("Fix video playback");
  await expect(workflow).toHaveText("No workflow");
  await refresh.click();
  await expect(workflow).toHaveText("quick-fix");
  expect((await workflowReceipt(weavie.workspace)).evidence).toEqual({
    prompt: "Fix video playback",
  });

  await writeFile(
    answerPath,
    JSON.stringify({ no_workflow: 0.95, "quick-fix": 0.025, "spec-driven": 0.025 }),
  );
  await refresh.click();
  await expect(workflow).toHaveText("No workflow");
  await prompt.fill("");
  await expect(refresh).toBeDisabled();
});

test("session fields have aligned labels and matching suggestion controls on desktop and phone", async ({
  page,
}) => {
  await page.locator(".session-rail-add").click();
  const inbox = page.locator(".session-inbox");
  for (const viewport of [
    { width: 1280, height: 800 },
    { width: 390, height: 844 },
  ]) {
    await page.setViewportSize(viewport);
    await expect(inbox).toBeVisible();
    for (const form of [
      inbox.locator(".session-composer"),
      inbox.locator(".session-open-branch"),
    ]) {
      await expect(form.getByText("Location", { exact: true })).toBeVisible();
      await expect(form.getByText("Agent", { exact: true })).toBeVisible();
    }
    const geometry = await inbox.evaluate((element) => {
      const rect = (node: Element) => {
        const { x, width, height } = node.getBoundingClientRect();
        return { x, width, height };
      };
      return {
        fields: [...element.querySelectorAll(".session-composer-field > .ui-field")].map(rect),
        suggestions: [...element.querySelectorAll(".session-composer-resuggest")].map((button) => ({
          button: rect(button),
          field: rect(button.parentElement!.querySelector(".ui-field")!),
        })),
        width: element.scrollWidth,
        visibleWidth: element.clientWidth,
      };
    });
    expect(geometry.fields.length).toBe(8);
    expect(new Set(geometry.fields.map((field) => field.x)).size).toBe(1);
    expect(geometry.suggestions).toHaveLength(2);
    for (const { button, field } of geometry.suggestions) {
      expect(button.height).toBe(field.height);
      expect(button.width).toBe(button.height);
    }
    expect(geometry.width).toBe(geometry.visibleWidth);
  }
});

test.describe("manual workflow suggestions", () => {
  test.use({ automaticInference: false });

  test("an explicit refresh works with automatic inference disabled and a short prompt", async ({
    page,
    weavie,
  }) => {
    await writeFile(
      join(weavie.workspace, "inference-answer.json"),
      JSON.stringify({ no_workflow: 0.05, "quick-fix": 0.9, "spec-driven": 0.05 }),
    );
    const surface = await createAcpSession(page, "manual-inference");
    await workflowTool(surface, "setSetting", { key: "inference.enabled", value: true });
    await page.locator(".session-rail-add").click();
    const inbox = page.locator(".session-inbox");
    await inbox.getByRole("combobox", { name: "Agent provider" }).selectOption("fake-acp");
    const prompt = inbox.getByRole("textbox", { name: "Prompt for a new session" });
    const refresh = inbox.getByRole("button", { name: "Suggest a workflow again" });
    const workflow = inbox.getByRole("button", { name: /^Workflow / });
    await expect(refresh).toBeDisabled();
    await prompt.fill("Fix video playback");
    await expect(refresh).toBeEnabled();
    await prompt.fill("");
    await expect(refresh).toBeDisabled();
    await prompt.fill("Fix video playback");
    await refresh.click();
    await expect(workflow).toHaveText("quick-fix");
    expect((await workflowReceipt(weavie.workspace)).evidence).toEqual({
      prompt: "Fix video playback",
    });
  });
});
