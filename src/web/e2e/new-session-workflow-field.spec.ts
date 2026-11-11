import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, type Page } from "@playwright/test";
import { CommandIds } from "../src/commands/types";
import { test } from "./harness/network-fixtures";
import { MockHost, mockSession, NEW_SESSION_COMMAND } from "./mock-host";

const distDir = join(dirname(fileURLToPath(import.meta.url)), "..", "dist");
const DRAFT =
  "the WebM video in the review pane fails to load whenever the diff is reopened twice in a row";
const CATALOG = {
  workflows: [
    { id: "broken", description: "", error: "run needs exactly one of prompt or skill", roles: [] },
    { id: "quick-fix", description: "A small, well-understood change.", error: null, roles: [] },
    {
      id: "spec-driven",
      description: "Requirements, design, and a task list.",
      error: null,
      roles: [
        { role: "planner", problem: null },
        { role: "builder", problem: 'The saved model "nova" is no longer offered.' },
      ],
    },
  ],
  default: null,
  defaultProblem: null,
};

const option = (id: string, label: string, description: string | null) => ({
  id,
  label,
  description,
  group: null,
});

/** The agent's pickers for a role: Astra offers an effort, Sol nothing more. */
function roleControls(model: string) {
  const models = [
    option("astra", "Astra", "Frontier intelligence."),
    option("sol", "Sol", "Workhorse."),
  ];
  const axis = (
    id: string,
    label: string,
    category: string,
    valueLabel: string,
    value: string,
  ) => ({
    id,
    label,
    description: null,
    category,
    kind: "select",
    value,
    valueLabel,
  });
  return {
    axes: [
      {
        ...axis(
          "model",
          "Model",
          "model",
          models.find((m) => m.id === model)?.label ?? "No model",
          model,
        ),
        options: models,
      },
      ...(model === "astra"
        ? [
            {
              ...axis("reasoning", "Effort", "thought_level", "Medium", ""),
              options: [
                option("", "Default (Medium)", null),
                option("low", "Low", null),
                option("high", "High", null),
              ],
            },
          ]
        : []),
    ],
    error: null,
  };
}

test.beforeAll(() => {
  if (!existsSync(join(distDir, "index.html"))) {
    throw new Error(
      `built app not found at ${distDir}; run \`pnpm run build\` before the e2e tests`,
    );
  }
});

async function openSessions(page: Page, host: MockHost): Promise<void> {
  host.setSessions([mockSession("main", "main", "acp")]);
  host.onHost("request", "git", "branches", (request) => host.respond(request, ["main"]));
  host.onHost("request", "sessionCreation", "workflows", (request) =>
    host.respond(request, CATALOG),
  );
  await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
  await host.waitUntilConnected();
  host.publishHost("commands", "catalog", { commands: [NEW_SESSION_COMMAND], keybindings: [] });
}

test("the composer suggests a workflow once, lets the keyboard change it, and starts the session in it", async ({
  page,
}) => {
  const host = await MockHost.start({ distDir });
  try {
    host.onHost("request", "sessionCreation", "previewBranch", (request) =>
      host.respond(request, { branch: "bug/webm-reload", error: null, needsMoreDetail: false }),
    );
    await page.setViewportSize({ width: 1200, height: 800 });
    await openSessions(page, host);
    await page.locator(".session-rail-add").click();
    const modal = page.getByRole("dialog", { name: "Sessions" });
    const inbox = page.locator(".session-inbox");
    const workflow = inbox.getByRole("button", { name: "Workflow" });
    await expect(workflow).toHaveText("No workflow");
    await expect(workflow).toBeEnabled();

    // One suggestion for the settled draft fills the field.
    const suggestion = host.waitForHost("request", "sessionCreation", "suggestWorkflow");
    await inbox.getByRole("textbox", { name: "Prompt for a new session" }).fill(DRAFT);
    const asked = await suggestion;
    expect(asked.payload).toEqual({
      sourceId: null,
      providerId: expect.any(String),
      prompt: DRAFT,
    });
    await expect(workflow).toContainText("Suggesting…");
    host.respond(asked, { applies: true, workflow: "quick-fix", error: null });
    await expect(workflow).toHaveText("quick-fix");

    // Reached by Tab from the branch row; the menu takes the keys while it is open, the dialog after.
    await inbox.getByRole("button", { name: "Suggest a branch name again" }).focus();
    await page.keyboard.press("Tab");
    await expect(workflow).toBeFocused();
    await page.keyboard.press("ArrowDown");
    const menu = page.getByRole("listbox", { name: "Workflow" });
    await expect(menu.getByRole("option")).toHaveText([
      /^No workflow.*ask it to create a workflow around it\.$/,
      /^quick-fix.*A small, well-understood change\.$/,
      /^spec-driven/,
      /^broken.*run needs exactly one of prompt or skill$/,
    ]);
    await expect(menu.locator(".control-menu-option-group")).toHaveText("Unavailable");
    await expect(menu.getByRole("option", { name: /quick-fix/ })).toHaveAttribute(
      "aria-selected",
      "true",
    );
    const broken = menu.getByRole("option", { name: /broken/ });
    await expect(broken).toHaveAttribute("aria-disabled", "true");
    await page.keyboard.press("Escape");
    await expect(menu).toBeHidden();
    await expect(modal).toBeVisible();

    // An unavailable workflow can't be picked; the one above it can.
    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("Enter");
    await expect(menu).toBeVisible();
    // The agent reports a role's pickers: its models, then what the chosen model offers.
    const models = host.waitForHost("request", "sessionCreation", "roleControls");
    const roleSuggestion = host.waitForHost("request", "sessionCreation", "suggestRole");
    await page.keyboard.press("ArrowUp");
    await page.keyboard.press("Enter");
    await expect(menu).toBeHidden();
    await expect(workflow).toHaveText("spec-driven");
    await expect(workflow).toBeFocused();
    expect(host.received.filter((message) => message.name === "suggestWorkflow")).toHaveLength(1);

    // The roles spec-driven still needs sit under their own heading, and Start waits for each one's model.
    await expect(inbox.getByText("Roles for spec-driven")).toBeVisible();
    const planner = inbox.getByRole("group", { name: "planner role" });
    const builder = inbox.getByRole("group", { name: "builder role" });
    const start = inbox.getByRole("button", { name: "Start", exact: true });
    await expect(start).toBeDisabled();
    host.onHost("request", "sessionCreation", "roleControls", (request) => {
      const { model } = request.payload as { model: string };
      host.respond(request, roleControls(model));
    });
    host.respond(await models, roleControls(""));
    await expect(builder.getByRole("button")).toHaveText(["ModelNo model"]);

    // Only the role with nothing saved is suggested; the gone model is the user's to replace.
    const askedRole = await roleSuggestion;
    expect(askedRole.payload).toMatchObject({ workflow: "spec-driven", role: "planner" });
    await expect(inbox.getByText("Suggesting…")).toBeVisible();
    host.respond(askedRole, { applies: true, model: "astra", error: null });
    await expect(planner.getByRole("button")).toHaveText(["ModelAstra", "EffortMedium"]);
    await expect(inbox.getByText('The saved model "nova" is no longer offered.')).toBeVisible();
    await expect(start).toBeDisabled();

    // The effort and model pickers are the agent's own, and what is picked is what a start saves.
    await planner.getByRole("button", { name: /Effort/ }).click();
    await page
      .getByRole("listbox", { name: "Effort" })
      .getByRole("option", { name: "High" })
      .click();
    await expect(planner.getByRole("button", { name: /Effort/ })).toHaveText("EffortHigh");
    // One menu at a time: opening another role's picker closes this one's, and a keyboard pick keeps the focus.
    const builderModel = builder.getByRole("button", { name: /Model/ });
    await builderModel.click();
    await planner.getByRole("button", { name: /Model/ }).click({ force: true });
    await expect(page.getByRole("listbox", { name: "Model" })).toHaveCount(1);
    await page.keyboard.press("Escape");
    await builderModel.focus();
    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("ArrowDown");
    await page.keyboard.press("Enter");
    await expect(builder.getByRole("button")).toHaveText(["ModelSol"]);
    await expect(builderModel).toBeFocused();
    await expect(planner.getByRole("button")).toHaveText(["ModelAstra", "EffortHigh"]);
    await expect(start).toBeEnabled();
    expect(host.received.filter((message) => message.name === "suggestRole")).toHaveLength(1);

    const invocation = host.waitForHost("request", "sessions", "invoke");
    await start.click();
    expect((await invocation).payload).toMatchObject({
      id: CommandIds.newSession,
      args: {
        branch: "bug/webm-reload",
        base: "main",
        existing: false,
        prompt: DRAFT,
        attachments: [],
        workflow: "spec-driven",
        roles: {
          planner: { model: "astra", options: { reasoning: "high" } },
          builder: { model: "sol", options: {} },
        },
      },
    });
  } finally {
    await host.close();
  }
});
