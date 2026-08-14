import { chmod, writeFile } from "node:fs/promises";
import { join } from "node:path";

export type FakeCodexInference = "disabled" | "failure" | "success";

const server = (inference: FakeCodexInference): string => String.raw`
const fs = require("node:fs");
const readline = require("node:readline");

const send = (value) => process.stdout.write(JSON.stringify(value) + "\n");
let turnSequence = 0;
const args = process.argv.slice(2);

if (args.includes("exec")) {
  process.stdin.resume();
  process.stdin.on("end", () => {
    const inference = ${JSON.stringify(inference)};
    if (inference !== "success") {
      send({ type: "error", message: "Reconnecting... 5/5" });
      send({
        type: "turn.failed",
        error: {
          message:
            "unexpected status 401 Unauthorized: Invalid API key: fake-secret, url: https://provider.invalid/v1/responses",
        },
      });
      process.exitCode = 7;
      return;
    }
    const exec = args.indexOf("exec");
    const approval = args.indexOf("--ask-for-approval");
    const outputFlag = args.indexOf("--output-last-message");
    const isolated = args.includes('permissions.weavie-inference.filesystem.:root="deny"')
      && !args.includes("tools.view_image=false");
    if (approval < 0 || approval > exec || outputFlag < 0 || !args.includes("--json") || !isolated) {
      process.exitCode = 2;
      return;
    }
    fs.writeFileSync(args[outputFlag + 1], JSON.stringify({ branch: "fix/mobile-branch-inference" }));
  });
} else readline.createInterface({ input: process.stdin }).on("line", (line) => {
  const message = JSON.parse(line);
  process.stderr.write("[fake-codex] " + (message.method ?? "response") + "\n");

  if (message.method === "initialize") {
    send({ id: message.id, result: { userAgent: "fake-codex" } });
    return;
  }
  if (message.method === "account/rateLimits/read") {
    send({
      id: message.id,
      result: {
        rateLimits: {
          limitId: "codex",
          limitName: null,
          primary: { usedPercent: 25, windowDurationMins: 300, resetsAt: 1786665600 },
          secondary: { usedPercent: 40, windowDurationMins: 10080, resetsAt: 1787184000 },
        },
      },
    });
    return;
  }
  if (message.method === "thread/start") {
    send({ id: message.id, result: { thread: { id: "thread_fake" } } });
    send({ method: "thread/started", params: { thread: { id: "thread_fake" } } });
    send({
      method: "thread/tokenUsage/updated",
      params: {
        threadId: "thread_fake",
        turnId: "startup",
        tokenUsage: {
          last: { totalTokens: 40000 },
          total: { totalTokens: 65000 },
          modelContextWindow: 200000,
        },
      },
    });
    return;
  }
  if (message.method === "model/list") {
    send({
      id: message.id,
      result: {
        data: [
          {
            id: "gpt-test",
            model: "gpt-test",
            displayName: "GPT Test",
            description: "Deterministic integration-test model.",
            hidden: false,
            isDefault: true,
            defaultReasoningEffort: "medium",
            supportedReasoningEfforts: [
              { reasoningEffort: "low", description: "Low effort" },
              { reasoningEffort: "medium", description: "Medium effort" },
              { reasoningEffort: "high", description: "High effort" },
            ],
            defaultServiceTier: "",
            serviceTiers: [],
          },
        ],
      },
    });
    return;
  }
  if (message.method === "collaborationMode/list") {
    send({
      id: message.id,
      result: {
        data: [
          { name: "Default", mode: "default", model: null, reasoning_effort: null },
          { name: "Plan", mode: "plan", model: null, reasoning_effort: "medium" },
        ],
      },
    });
    return;
  }
  if (message.method === "skills/list") {
    send({
      id: message.id,
      result: { data: [{ cwd: process.cwd(), errors: [], skills: [] }] },
    });
    return;
  }
  if (message.method === "turn/start") {
    const turnId = "turn_" + ++turnSequence;
    const text = message.params.input.find((item) => item.type === "text")?.text ?? "";
    send({ id: message.id, result: { turn: { id: turnId } } });
    send({
      method: "turn/started",
      params: { threadId: "thread_fake", turn: { id: turnId, status: "running" } },
    });
    send({
      method: "thread/tokenUsage/updated",
      params: {
        threadId: "thread_fake",
        turnId,
        tokenUsage: {
          last: { totalTokens: 100000 },
          total: { totalTokens: 150000 },
          modelContextWindow: 200000,
        },
      },
    });
    send({
      method: "item/completed",
      params: {
        threadId: "thread_fake",
        turnId,
        item: {
          id: "item_" + turnSequence,
          type: "agentMessage",
          status: "completed",
          text: "echo: " + text,
        },
      },
    });
    send({
      method: "turn/completed",
      params: { threadId: "thread_fake", turn: { id: turnId, status: "completed" } },
    });
  }
});
`;

export async function writeFakeCodexWrapper(
  dir: string,
  inference: FakeCodexInference,
): Promise<string> {
  const script = join(dir, "fake-codex.cjs");
  await writeFile(script, server(inference));
  if (process.platform === "win32") {
    const wrapper = join(dir, "fake-codex.cmd");
    await writeFile(wrapper, `@${JSON.stringify(process.execPath)} "${script}" %*\r\n`);
    return wrapper;
  }
  const wrapper = join(dir, "fake-codex.sh");
  await writeFile(
    wrapper,
    `#!/bin/sh\nexec ${JSON.stringify(process.execPath)} ${JSON.stringify(script)} "$@"\n`,
  );
  await chmod(wrapper, 0o755);
  return wrapper;
}
