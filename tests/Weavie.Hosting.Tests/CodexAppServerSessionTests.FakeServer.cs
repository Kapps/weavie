namespace Weavie.Hosting.Tests;

public sealed partial class CodexAppServerSessionTests {
	private const string FakeServerScript = """
const fs = require("fs");
const readline = require("readline");
function send(value) {
  process.stdout.write(JSON.stringify(value) + "\n");
}
readline.createInterface({ input: process.stdin }).on("line", line => {
  const message = JSON.parse(line);
  if (message.method === "initialize") {
    send({ id: message.id, result: { userAgent: "fake-codex" } });
  } else if (message.method === "hooks/list") {
    const unsafe = fs.existsSync("unsafe-hooks");
    send({ id: message.id, result: { data: [{ cwd: process.cwd(), errors: [], warnings: [], hooks: unsafe ? [
      { enabled: true, isManaged: false, source: "user", trustStatus: "untrusted", command: "evil", eventName: "preToolUse", handlerType: "command", key: "user:1", sourcePath: process.cwd(), currentHash: "h", displayOrder: 1, timeoutSec: 30 }
    ] : [
      { enabled: true, isManaged: false, source: "sessionFlags", trustStatus: "untrusted", command: "weavie", eventName: "preToolUse", handlerType: "command", key: "session:1", sourcePath: process.cwd(), currentHash: "h", displayOrder: 1, timeoutSec: 30 }
    ] }] } });
  } else if (message.method === "thread/start") {
    fs.writeFileSync("thread-start.json", JSON.stringify(message));
    if (fs.existsSync("start-fails")) {
      send({ id: message.id, error: { code: -32600, message: "Invalid request: unknown variant `on-failure`" } });
    } else {
      send({ id: message.id, result: { thread: { id: "thread_fake" } } });
    }
  } else if (message.method === "thread/resume") {
    fs.writeFileSync("thread-resume.json", JSON.stringify(message));
    if (fs.existsSync("resume-fails")) {
      send({ id: message.id, error: { code: -32603, message: "failed to read thread: thread-store internal error: rollout is empty" } });
      return;
    }
    const turns = fs.existsSync("resume-with-history") ? [{ id: "turn_old", status: "completed", items: [
      { type: "userMessage", id: "user_old", content: [{ type: "text", text: "old prompt", text_elements: [] }] },
      { type: "agentMessage", id: "agent_old", text: "old answer" }
    ] }] : [];
    send({ id: message.id, result: { thread: { id: message.params.threadId, turns } } });
  } else if (message.method === "model/list") {
    send({ id: message.id, result: { data: [
      { id: "gpt-5.5", model: "gpt-5.5", displayName: "GPT-5.5", description: "Frontier model.", hidden: false, isDefault: true },
      { id: "gpt-5.4-mini", model: "gpt-5.4-mini", displayName: "GPT-5.4 mini", description: "Fast model.", hidden: false, isDefault: false }
    ] } });
  } else if (message.method === "skills/list") {
    fs.writeFileSync("skills-list.json", JSON.stringify(message));
    send({ id: message.id, result: { data: [{ cwd: process.cwd(), errors: [], skills: [
      { name: "review-pr", description: "Review a PR.", enabled: true, path: process.cwd(), scope: "repo", interface: { shortDescription: "Review a pull request.", defaultPrompt: "Review the current PR." } }
    ] }] } });
  } else if (message.method === "turn/start") {
    fs.writeFileSync("turn-start.json", JSON.stringify(message));
    send({ id: message.id, result: { turn: { id: "turn_fake" } } });
    send({ method: "turn/started", params: { threadId: "thread_fake", turn: { id: "turn_fake", status: "running" } } });
    if (message.params.input.some(item => item.type === "localImage")) {
      fs.writeFileSync("image-turn.json", JSON.stringify(message));
    } else if (message.params.input[0].text === "out of tokens") {
      const error = { message: "You have no weighted tokens left", codexErrorInfo: "usageLimitExceeded", additionalDetails: null };
      send({ method: "error", params: { threadId: "thread_fake", turnId: "turn_fake", willRetry: false, error } });
      send({ method: "turn/completed", params: { threadId: "thread_fake", turn: { id: "turn_fake", status: "failed", error } } });
    } else if (message.params.input[0].text === "approval") {
      send({ id: "approval-1", method: "item/commandExecution/requestApproval", params: { threadId: "thread_fake", turnId: "turn_fake", itemId: "item_fake", startedAtMs: 1, command: "dotnet test", cwd: process.cwd(), reason: "test" } });
    } else if (message.params.input[0].text === "unsupported") {
      send({ id: "unsupported-1", method: "item/tool/call", params: { threadId: "thread_fake", turnId: "turn_fake", itemId: "item_fake" } });
    }
  } else if (message.method === "turn/steer") {
    fs.writeFileSync("turn-steer.json", JSON.stringify(message));
    if (message.params.input[0].text === "stale steer") {
      send({ id: message.id, error: { code: -32600, message: "expected active turn id `turn_new` but found `" + message.params.expectedTurnId + "`" } });
    } else {
      send({ id: message.id, result: {} });
    }
  } else if (message.id === "approval-1") {
    fs.writeFileSync("approval-response.json", JSON.stringify(message));
  }
});
""";
}
