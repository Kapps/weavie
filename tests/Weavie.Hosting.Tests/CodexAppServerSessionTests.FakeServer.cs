namespace Weavie.Hosting.Tests;

public sealed partial class CodexAppServerSessionTests {
	private const string FakeServerScript = """
const fs = require("fs");
const readline = require("readline");
function send(value) {
  process.stdout.write(JSON.stringify(value) + "\n");
}
// Write-then-rename so tests polling File.Exists never read a half-written file.
function record(name, value) {
  fs.writeFileSync(name + ".tmp", JSON.stringify(value));
  fs.renameSync(name + ".tmp", name);
}
record("app-server-args.json", process.argv.slice(2));
readline.createInterface({ input: process.stdin }).on("line", line => {
  const message = JSON.parse(line);
  if (message.method === "initialize") {
    send({ id: message.id, result: { userAgent: "fake-codex" } });
  } else if (message.method === "thread/start") {
    record("thread-start.json", message);
    if (fs.existsSync("start-fails")) {
      send({ id: message.id, error: { code: -32600, message: "Invalid request: unknown variant `on-failure`" } });
    } else {
      send({ id: message.id, result: { thread: { id: "thread_fake" } } });
      send({ method: "thread/started", params: { thread: { id: "thread_fake" } } });
    }
  } else if (message.method === "thread/resume") {
    record("thread-resume.json", message);
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
    record("skills-list.json", message);
    send({ id: message.id, result: { data: [{ cwd: process.cwd(), errors: [], skills: [
      { name: "review-pr", description: "Review a PR.", enabled: true, path: process.cwd(), scope: "repo", interface: { shortDescription: "Review a pull request.", defaultPrompt: "Review the current PR." } }
    ] }] } });
  } else if (message.method === "turn/start") {
    record("turn-start.json", message);
    if (message.params.input[0].text === "delay start") {
      record("turn-start-pending.json", message);
      const release = setInterval(() => {
        if (fs.existsSync("release-turn-start")) {
          clearInterval(release);
          send({ id: message.id, result: { turn: { id: "turn_fake" } } });
          send({ method: "turn/started", params: { threadId: "thread_fake", turn: { id: "turn_fake", status: "running" } } });
        }
      }, 5);
      return;
    }
    send({ id: message.id, result: { turn: { id: "turn_fake" } } });
    send({ method: "turn/started", params: { threadId: "thread_fake", turn: { id: "turn_fake", status: "running" } } });
    if (message.params.input[0].text === "subagent") {
      send({ method: "thread/started", params: { thread: { id: "thread_sub" } } });
      send({ method: "turn/started", params: { threadId: "thread_sub", turn: { id: "turn_sub", status: "running" } } });
      send({ method: "item/completed", params: { threadId: "thread_sub", turnId: "turn_sub", item: { id: "sub_message", type: "agentMessage", status: "completed", text: "Subagent update" } } });
      send({ method: "turn/completed", params: { threadId: "thread_sub", turn: { id: "turn_sub", status: "completed" } } });
    } else if (message.params.input.some(item => item.type === "localImage")) {
      record("image-turn.json", message);
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
    } else if (message.params.input[0].text === "file approval") {
      send({ method: "item/started", params: { threadId: "thread_fake", turnId: "turn_fake", item: { type: "fileChange", id: "item_edit", status: "inProgress", changes: [{ path: "src/App.cs", kind: "update" }, { path: "src/Program.cs", kind: "update" }] } } });
      send({ id: "approval-2", method: "item/fileChange/requestApproval", params: { threadId: "thread_fake", turnId: "turn_fake", itemId: "item_edit", startedAtMs: 1, reason: "apply the patch" } });
    } else if (message.params.input[0].text === "file approval collision") {
      send({ method: "item/started", params: { threadId: "thread_fake", turnId: "turn_fake", item: { type: "fileChange", id: "item_collision", status: "inProgress", changes: [{ path: "src/Root.cs", kind: "update" }] } } });
      send({ method: "item/started", params: { threadId: "thread_sub", turnId: "turn_sub", item: { type: "fileChange", id: "item_collision", status: "inProgress", changes: [{ path: "src/Subagent.cs", kind: "update" }] } } });
      send({ id: "approval-4", method: "item/fileChange/requestApproval", params: { threadId: "thread_fake", turnId: "turn_fake", itemId: "item_collision", startedAtMs: 1, reason: "apply the root patch" } });
    } else if (message.params.input[0].text === "approval then crash") {
      send({ id: "approval-3", method: "item/commandExecution/requestApproval", params: { threadId: "thread_fake", turnId: "turn_fake", itemId: "item_crash", startedAtMs: 1, command: "dotnet test", cwd: process.cwd(), reason: "test" } });
      setTimeout(() => process.exit(7), 100);
    } else if (message.params.input[0].text === "unsupported") {
      send({ id: "unsupported-1", method: "item/tool/call", params: { threadId: "thread_fake", turnId: "turn_fake", itemId: "item_fake" } });
    }
  } else if (message.method === "turn/steer") {
    record("turn-steer.json", message);
    if (message.params.input[0].text === "stale steer") {
      send({ id: message.id, error: { code: -32600, message: "expected active turn id `turn_new` but found `" + message.params.expectedTurnId + "`" } });
    } else {
      send({ id: message.id, result: {} });
    }
  } else if (message.id === "approval-1") {
    record("approval-response.json", message);
  }
});
""";
}
