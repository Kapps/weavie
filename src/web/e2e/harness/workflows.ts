import { mkdir, readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Locator, Page } from "@playwright/test";
import { createAcpSession, reloadAcpProviders, submitAcpDraft } from "./acp-session";
import { expect } from "./fixtures";
import { sessionWorktrees } from "./git-workspace";
import { fakeAcpProgram } from "./test-programs";

/** Installs the isolated workflow inference fake independently of the interactive agent. */
export async function installWorkflowInference(page: Page, home: string): Promise<void> {
  const path = join(home, ".weavie", "acp", "custom.json");
  const registry = JSON.parse(await readFile(path, "utf8"));
  registry.agents.push({
    id: "workflow-acp",
    name: "Workflow ACP inference",
    command: fakeAcpProgram.command,
    args: [...fakeAcpProgram.args, "inference", "workflow"],
    env: {},
  });
  await writeFile(path, JSON.stringify(registry));
  await reloadAcpProviders(page);
}

/** Creates an interactive session with isolated workflow inference enabled. */
export async function createWorkflowSession(
  page: Page,
  home: string,
  workspace: string,
  provider: string,
) {
  if (provider === "workflow-acp") await installWorkflowInference(page, home);
  const surface = await createAcpSession(page, "workflow-test");
  const worktree = sessionWorktrees(workspace)[0]!;
  await workflowTool(surface, "setSetting", { key: "inference.enabled", value: true });
  await workflowTool(surface, "setSetting", { key: "inference.defaultProvider", value: provider });
  return { surface, worktree, run: surface.locator(".pane-head .pane-run") };
}

/** Calls a registry tool through the interactive fake agent and waits for its successful reply. */
export async function workflowTool(surface: Locator, name: string, args: unknown): Promise<string> {
  const replies = surface
    .locator(".agent-virtual-row:has(.agent-entry-message.agent-tone-assistant)")
    .filter({ hasText: new RegExp(`${name}( failed)?:`) });
  const seen = await replies.evaluateAll((rows) =>
    rows.map((row) => row.getAttribute("data-transcript-entry")),
  );
  await submitAcpDraft(surface, `mcp:${name}:${JSON.stringify(args)}`);
  let reply: string | null | undefined;
  await expect
    .poll(async () => {
      reply = await replies.evaluateAll(
        (rows, ids) =>
          rows.find((row) => !ids.includes(row.getAttribute("data-transcript-entry")))?.textContent,
        seen,
      );
      return reply;
    })
    .toBeTruthy();
  expect(reply).not.toContain(`${name} failed:`);
  return reply!;
}

/** Saves a workflow in the session worktree. */
export async function writeWorkflow(worktree: string, id: string, stages: string): Promise<void> {
  await writeWorkflowSource(
    worktree,
    id,
    `id: ${id}\ndescription: Exercise workflow decisions.\nstages:\n${stages}`,
  );
}

/** Saves a complete workflow definition, including inputs, in the session worktree. */
export async function writeWorkflowSource(
  worktree: string,
  id: string,
  source: string,
): Promise<void> {
  const directory = join(worktree, ".weavie", "workflows");
  await mkdir(directory, { recursive: true });
  await writeFile(join(directory, `${id}.yaml`), source);
}

/** Reads only the feature's input JSON from the provider's complete process receipt. */
export async function workflowReceipt(worktree: string) {
  const receipt = await readFile(join(worktree, "inference-receipt.txt"), "utf8");
  return JSON.parse(receipt.split("Input JSON:\n")[1]!.split("\n\nRespond with exactly")[0]!);
}
