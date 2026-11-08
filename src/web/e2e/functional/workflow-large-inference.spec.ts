import { createHash } from "node:crypto";
import { existsSync } from "node:fs";
import { mkdir, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { expect, test } from "../harness/fixtures";
import { installWorkflowInference, workflowReceipt } from "../harness/workflows";

const EVIDENCE = `BEGIN\n${"Evidence café α 🧪\n".repeat(100_000)}END`;
const SUMMARY = `Review all ${Buffer.byteLength(EVIDENCE)} UTF-8 bytes of evidence.`;
const QUESTION = "Does the evidence start with BEGIN and finish with END?";
const WORKFLOW = `id: large-evidence
description: Decide from a complete multi-megabyte text output.
stages:
  - id: collect
    run:
      prompt: Collect the evidence.
      outputs: { evidence: text }
    then:
      ask: ${QUESTION}
      by: model
      choices:
        accept: accepted
        reject: rejected
  - id: accepted
    run:
      prompt: The model accepted the complete evidence.
  - id: rejected
    run:
      prompt: The model rejected the evidence.
`;

const sha256 = (text: string) => createHash("sha256").update(text).digest("hex");

for (const provider of ["claude", "workflow-acp"]) {
  for (const rejects of [false, true]) {
    test.describe(`${provider} ${rejects ? "provider rejection" : "successful routing"}`, () => {
      test.use({
        inference: "workflow",
        fakeScript: {
          steps: [
            { op: "hook", request: { hook_event_name: "SessionStart", source: "startup" } },
            { op: "waitFile", path: "start-large-decision" },
            { op: "mcp", tool: "setSetting", args: { key: "inference.enabled", value: true } },
            {
              op: "mcp",
              tool: "setSetting",
              args: { key: "inference.defaultProvider", value: provider },
            },
            {
              op: "mcp",
              tool: "runCommand",
              args: { id: "weavie.workflow.run", args: { workflow: "large-evidence", inputs: {} } },
            },
            { op: "waitInput", text: "Collect the evidence." },
            { op: "waitFile", path: "complete-large-decision" },
            {
              op: "mcp",
              tool: "completeStage",
              args: { outputs: { evidence: EVIDENCE }, summary: SUMMARY },
            },
            { op: "hook", request: { hook_event_name: "Stop" } },
            {
              op: "waitInput",
              text: rejects
                ? "Weavie now asks the user:"
                : "The model accepted the complete evidence.",
            },
          ],
        },
      });

      test("delivers the entire large output and surfaces the provider outcome", async ({
        page,
        weavie,
      }, testInfo) => {
        if (provider === "workflow-acp") {
          await installWorkflowInference(page, weavie.home);
        }

        const workflows = join(weavie.workspace, ".weavie", "workflows");
        await mkdir(workflows, { recursive: true });
        await writeFile(join(workflows, "large-evidence.yaml"), WORKFLOW);
        if (rejects) {
          await writeFile(join(weavie.workspace, "inference-reject"), "");
        }
        await writeFile(join(weavie.workspace, "start-large-decision"), "");
        const run = page.locator(".agent-terminal-surface .pane-head .pane-run");
        await expect(run).toHaveText("large-evidence · collect");
        await expect.poll(() => weavie.fakeLog()).toMatch(/input .*Collect the evidence\./);
        await writeFile(join(weavie.workspace, "complete-large-decision"), "");

        const receiptPath = join(weavie.workspace, "inference-receipt.txt");
        await expect.poll(() => existsSync(receiptPath)).toBe(true);
        if (rejects) {
          await expect(run).toHaveAttribute("title", /collect · needs your answer/);
          await expect(run).toHaveAttribute("title", /decision failed \(ProviderUnavailable:/);
          await expect.poll(() => weavie.fakeLog()).toContain("Weavie now asks the user:");
          await expect.poll(() => weavie.fakeLog()).toContain(QUESTION);
        } else {
          await expect(run).toHaveText("large-evidence · accepted");
          await expect
            .poll(() => weavie.fakeLog())
            .toMatch(/input .*The model accepted the complete evidence\./);
        }

        const input = await workflowReceipt(weavie.workspace);
        expect(input.question).toBe(QUESTION);
        expect(input.evidence.summary).toBe(SUMMARY);
        expect(Buffer.byteLength(input.evidence.outputs.evidence)).toBe(
          Buffer.byteLength(EVIDENCE),
        );
        expect(sha256(input.evidence.outputs.evidence)).toBe(sha256(EVIDENCE));
        await testInfo.attach("provider-payload-receipt", {
          body: JSON.stringify({
            provider,
            rejects,
            bytes: Buffer.byteLength(input.evidence.outputs.evidence),
            sha256: sha256(input.evidence.outputs.evidence),
          }),
          contentType: "application/json",
        });
      });
    });
  }
}
