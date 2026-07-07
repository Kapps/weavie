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
    send({ id: message.id, result: { thread: { id: "thread_fake" } } });
  } else if (message.method === "thread/resume") {
    fs.writeFileSync("thread-resume.json", JSON.stringify(message));
    send({ id: message.id, result: { thread: { id: message.params.threadId } } });
  } else if (message.method === "turn/start") {
    send({ id: message.id, result: { turn: { id: "turn_fake" } } });
    send({ method: "turn/started", params: { threadId: "thread_fake", turn: { id: "turn_fake", status: "running" } } });
    if (message.params.input.some(item => item.type === "localImage")) {
      fs.writeFileSync("image-turn.json", JSON.stringify(message));
    } else if (message.params.input[0].text === "approval") {
      send({ id: "approval-1", method: "item/commandExecution/requestApproval", params: { threadId: "thread_fake", turnId: "turn_fake", itemId: "item_fake", startedAtMs: 1, command: "dotnet test", cwd: process.cwd(), reason: "test" } });
    } else if (message.params.input[0].text === "unsupported") {
      send({ id: "unsupported-1", method: "item/tool/call", params: { threadId: "thread_fake", turnId: "turn_fake", itemId: "item_fake" } });
    }
  } else if (message.id === "approval-1") {
    fs.writeFileSync("approval-response.json", JSON.stringify(message));
  }
});
""";
}
