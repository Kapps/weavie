namespace Weavie.Hosting.Tests;

public sealed partial class CodexAppServerSessionTests {
	private const string FakeServerScript = """
const fs = require("fs");
const readline = require("readline");
function send(value) {
  process.stdout.write(JSON.stringify(value) + "\n");
}
function capture(path, value) {
  fs.writeFileSync(path + ".tmp", JSON.stringify(value));
  fs.renameSync(path + ".tmp", path);
}
readline.createInterface({ input: process.stdin }).on("line", line => {
  const message = JSON.parse(line);
  if (message.method === "initialize") {
    send({ id: message.id, result: { userAgent: "fake-codex" } });
  } else if (message.method === "thread/start") {
    capture("thread-start.json", message);
    if (fs.existsSync("start-fails")) {
      send({ id: message.id, error: { code: -32600, message: "Invalid request: unknown variant `on-failure`" } });
    } else {
      send({ id: message.id, result: { thread: { id: "thread_fake" } } });
      send({ method: "thread/started", params: { thread: { id: "thread_fake" } } });
    }
  } else if (message.method === "thread/resume") {
    capture("thread-resume.json", message);
    if (fs.existsSync("resume-fails")) {
      send({ id: message.id, error: { code: -32603, message: "failed to read thread: thread-store internal error: rollout is empty" } });
      return;
    }
    const turns = fs.existsSync("resume-with-history") ? [{ id: "turn_old", status: "completed", items: [
      { type: "userMessage", id: "user_old", content: [{ type: "text", text: "old prompt", text_elements: [] }] },
      { type: "agentMessage", id: "agent_old", text: "old answer" }
    ] }] : [];
    send({ id: message.id, result: { thread: { id: message.params.threadId, turns } } });
    send({ method: "thread/started", params: { thread: { id: message.params.threadId } } });
  } else if (message.method === "model/list") {
    const efforts = levels => levels.map(effort => ({ reasoningEffort: effort, description: effort + " effort" }));
    send({ id: message.id, result: { data: [
      { id: "gpt-5.5", model: "gpt-5.5", displayName: "GPT-5.5", description: "Frontier model.", hidden: false, isDefault: true,
        defaultReasoningEffort: "medium", supportedReasoningEfforts: efforts(["low", "medium", "high"]),
        defaultServiceTier: "", serviceTiers: [{ id: "priority", name: "Fast", description: "1.5x speed, increased usage" }] },
      { id: "gpt-5.4-mini", model: "gpt-5.4-mini", displayName: "GPT-5.4 mini", description: "Fast model.", hidden: false, isDefault: false,
        defaultReasoningEffort: "low", supportedReasoningEfforts: efforts(["low", "medium"]),
        defaultServiceTier: "", serviceTiers: [] }
    ] } });
  } else if (message.method === "skills/list") {
    capture("skills-list.json", message);
    send({ id: message.id, result: { data: [{ cwd: process.cwd(), errors: [], skills: [
      { name: "review-pr", description: "Review a PR.", enabled: true, path: process.cwd(), scope: "repo", interface: { shortDescription: "Review a pull request.", defaultPrompt: "Review the current PR." } }
    ] }] } });
  } else if (message.method === "turn/start") {
    capture("turn-start.json", message);
    send({ id: message.id, result: { turn: { id: "turn_fake" } } });
    send({ method: "turn/started", params: { threadId: "thread_fake", turn: { id: "turn_fake", status: "running" } } });
    if (message.params.input[0].text === "subagent") {
      send({ method: "thread/started", params: { thread: { id: "thread_sub" } } });
      send({ method: "turn/started", params: { threadId: "thread_sub", turn: { id: "turn_sub", status: "running" } } });
      send({ method: "item/completed", params: { threadId: "thread_sub", turnId: "turn_sub", item: { id: "sub_message", type: "agentMessage", status: "completed", text: "Subagent update" } } });
      send({ method: "turn/completed", params: { threadId: "thread_sub", turn: { id: "turn_sub", status: "completed" } } });
    } else if (message.params.input.some(item => item.type === "localImage")) {
      capture("image-turn.json", message);
    } else if (message.params.input[0].text === "out of tokens") {
      const error = { message: "You have no weighted tokens left", codexErrorInfo: "usageLimitExceeded", additionalDetails: null };
      send({ method: "error", params: { threadId: "thread_fake", turnId: "turn_fake", willRetry: false, error } });
      send({ method: "turn/completed", params: { threadId: "thread_fake", turn: { id: "turn_fake", status: "failed", error } } });
    } else if (message.params.input[0].text === "approval") {
      send({ id: "approval-1", method: "item/commandExecution/requestApproval", params: { threadId: "thread_fake", turnId: "turn_fake", itemId: "item_fake", startedAtMs: 1, command: "dotnet test", cwd: process.cwd(), reason: "test" } });
    } else if (message.params.input[0].text === "server resolves approval" || message.params.input[0].text === "server resolves subapproval") {
      const subagent = message.params.input[0].text === "server resolves subapproval";
      const threadId = subagent ? "thread_sub" : "thread_fake";
      const turnId = subagent ? "turn_sub" : "turn_fake";
      send({ id: "cleanup-1", method: "item/commandExecution/requestApproval", params: { threadId, turnId, itemId: "item_cleanup", startedAtMs: 1, command: "dotnet test", cwd: process.cwd(), reason: "test" } });
      send({ method: "serverRequest/resolved", params: { threadId, requestId: "cleanup-1" } });
    } else if (message.params.input[0].text === "unsupported") {
      send({ id: "unsupported-1", method: "item/tool/call", params: { threadId: "thread_fake", turnId: "turn_fake", itemId: "item_fake" } });
    }
  } else if (message.method === "turn/steer") {
    capture("turn-steer.json", message);
    if (message.params.input[0].text === "stale steer") {
      send({ id: message.id, error: { code: -32600, message: "expected active turn id `turn_new` but found `" + message.params.expectedTurnId + "`" } });
    } else {
      send({ id: message.id, result: {} });
    }
  } else if (message.id === "approval-1") {
    capture("approval-response.json", message);
  }
});
""";
}
