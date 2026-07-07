using System.Text.Json;
using Weavie.Hosting.Agents.Codex;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class CodexAppServerClientTests : IDisposable {
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "weavie-codex-client-tests", Guid.NewGuid().ToString("N"));

	public CodexAppServerClientTests() {
		Directory.CreateDirectory(_dir);
		File.WriteAllText(Path.Combine(_dir, "app-server"), FakeServerScript);
	}

	public void Dispose() {
		try {
			Directory.Delete(_dir, recursive: true);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	[Fact]
	public async Task ExchangesRequestsNotificationsAndRestarts() {
		var notifications = new List<string>();
		var starts = new List<int>();
		var logs = new List<string>();
		await using var client = new CodexAppServerClient(
			"node",
			_dir,
			["--no-warnings"],
			["-c", "mcp_servers.weavie.enabled=true"],
			["-c", "hooks.PreToolUse=[]"],
			new Dictionary<string, string>(StringComparer.Ordinal) { ["WEAVIE_HOOK_PIPE"] = "weavie-hook-test" },
			logs.Add);
		client.ProcessStarted += starts.Add;
		client.NotificationReceived += root => {
			if (root.GetProperty("method").GetString() == "turn/started") {
				throw new InvalidOperationException("boom");
			}
		};
		client.NotificationReceived += root => notifications.Add(root.GetProperty("method").GetString() ?? string.Empty);

		client.Start();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		var initialized = await client.RequestAsync(
			1,
			"""{"id":1,"method":"initialize","params":{"clientInfo":{"name":"weavie-test","title":"Weavie Test","version":"0"}}}""",
			timeout.Token);
		client.Notify("""{"method":"initialized","params":{}}""");
		var thread = await client.RequestAsync(
			2,
			"""{"id":2,"method":"thread/start","params":{"cwd":"/repo"}}""",
			timeout.Token);
		await client.RequestAsync(
			3,
			"""{"id":3,"method":"turn/start","params":{"threadId":"thread_fake","input":[{"type":"text","text":"hi"}]}}""",
			timeout.Token);
		await client.RequestAsync(
			4,
			"""{"id":4,"method":"turn/interrupt","params":{"threadId":"thread_fake","turnId":"turn_fake"}}""",
			timeout.Token);
		await Assert.ThrowsAsync<IOException>(() => client.RequestAsync(
			5,
			"""{"id":5,"method":"test/exit","params":{}}""",
			timeout.Token));
		await WaitForAsync(() => starts.Count >= 2);

		Assert.Equal("fake-codex", initialized.GetProperty("userAgent").GetString());
		Assert.Equal("thread_fake", thread.GetProperty("thread").GetProperty("id").GetString());
		Assert.Contains("turn/started", notifications);
		Assert.Contains("item/agentMessage/delta", notifications);
		Assert.Contains("turn/completed", notifications);
		Assert.Contains("turn/interrupted", notifications);
		Assert.Contains(logs, line => line.Contains("exited 7", StringComparison.Ordinal));
		Assert.Contains(logs, line => line.Contains("notification handler failed: boom", StringComparison.Ordinal));
		using var argsDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "args.json")));
		using var execArgsDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "exec-args.json")));
		Assert.Equal("--no-warnings", execArgsDoc.RootElement[0].GetString());
		Assert.Equal("-c", argsDoc.RootElement[0].GetString());
		Assert.Contains(argsDoc.RootElement.EnumerateArray(), value => value.GetString() == "hooks.PreToolUse=[]");
		Assert.Contains(argsDoc.RootElement.EnumerateArray(), value => value.GetString() == "--stdio");
		Assert.Equal("weavie-hook-test", File.ReadAllText(Path.Combine(_dir, "env.txt")));
	}

	[Fact]
	public async Task NotificationHandlerFailure_DoesNotStopOtherSubscribers() {
		var notifications = new List<string>();
		var logs = new List<string>();
		await using var client = EmptyClient(logs.Add);
		client.NotificationReceived += _ => throw new InvalidOperationException("boom");
		client.NotificationReceived += root => notifications.Add(root.GetProperty("method").GetString() ?? string.Empty);

		client.Start();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		await client.RequestAsync(
			1,
			"""{"id":1,"method":"initialize","params":{"clientInfo":{"name":"weavie-test","title":"Weavie Test","version":"0"}}}""",
			timeout.Token);
		client.Notify("""{"method":"initialized","params":{}}""");
		await client.RequestAsync(
			2,
			"""{"id":2,"method":"turn/start","params":{"threadId":"thread_fake","input":[{"type":"text","text":"hi"}]}}""",
			timeout.Token);
		await WaitForAsync(() => notifications.Contains("turn/completed"));

		Assert.Contains("turn/started", notifications);
		Assert.Contains(logs, line => line.Contains("notification handler failed: boom", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Restart_StartsFreshServer() {
		var starts = new List<int>();
		await using var client = EmptyClient(_ => { });
		client.ProcessStarted += starts.Add;

		client.Start();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		var first = await client.RequestAsync(
			1,
			"""{"id":1,"method":"initialize","params":{"clientInfo":{"name":"weavie-test","title":"Weavie Test","version":"0"}}}""",
			timeout.Token);

		client.Restart();
		await WaitForAsync(() => starts.Count >= 2);
		var second = await client.RequestAsync(
			2,
			"""{"id":2,"method":"initialize","params":{"clientInfo":{"name":"weavie-test","title":"Weavie Test","version":"0"}}}""",
			timeout.Token);

		Assert.Equal("fake-codex", first.GetProperty("userAgent").GetString());
		Assert.Equal("fake-codex", second.GetProperty("userAgent").GetString());
		Assert.Equal([0, 0], starts);
	}

	[Fact]
	public async Task Restart_FailsPendingRequests() {
		await using var client = EmptyClient(_ => { });

		client.Start();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		var pending = client.RequestAsync(
			1,
			"""{"id":1,"method":"test/hang","params":{}}""",
			timeout.Token);
		client.Restart();

		var error = await Assert.ThrowsAsync<IOException>(() => pending);
		Assert.Equal("Codex app-server restarted.", error.Message);
	}

	[Fact]
	public async Task ServerRequest_WithStringId_IsAnsweredWithStringId() {
		var requests = new List<CodexServerRequest>();
		var logs = new List<string>();
		await using var client = EmptyClient(logs.Add);
		client.RequestReceived += request => {
			requests.Add(request);
			client.Respond(request.ResponseId, new { decision = "accept" });
		};

		client.Start();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		await client.RequestAsync(
			1,
			"""{"id":1,"method":"test/string-request","params":{}}""",
			timeout.Token);
		await WaitForAsync(() => requests.Count == 1);
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "string-response.json")));

		var request = Assert.Single(requests);
		Assert.Equal("approval-1", request.Id);
		Assert.DoesNotContain(logs, line => line.Contains("request handler failed", StringComparison.Ordinal));
		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "string-response.json")));
		Assert.Equal("approval-1", doc.RootElement.GetProperty("id").GetString());
		Assert.Equal("accept", doc.RootElement.GetProperty("result").GetProperty("decision").GetString());
	}

	[Fact]
	public async Task ServerRequest_CanBeAnsweredWithJsonRpcError() {
		await using var client = EmptyClient(_ => { });
		client.RequestReceived += request => client.RespondError(request.ResponseId, -32601, "unsupported");

		client.Start();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		await client.RequestAsync(
			1,
			"""{"id":1,"method":"test/error-request","params":{}}""",
			timeout.Token);
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "error-response.json")));

		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "error-response.json")));
		Assert.Equal("unsupported-1", doc.RootElement.GetProperty("id").GetString());
		Assert.Equal(-32601, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
		Assert.Equal("unsupported", doc.RootElement.GetProperty("error").GetProperty("message").GetString());
	}

	private static async Task WaitForAsync(Func<bool> done) {
		for (int i = 0; i < 80; i++) {
			if (done()) {
				return;
			}

			await Task.Delay(25);
		}

		throw new TimeoutException("Condition was not met within the timeout.");
	}

	private CodexAppServerClient EmptyClient(Action<string> log) =>
		new("node", _dir, [], [], [], new Dictionary<string, string>(StringComparer.Ordinal), log);

	private const string FakeServerScript = """
const fs = require("fs");
const readline = require("readline");
fs.writeFileSync("args.json", JSON.stringify(process.argv.slice(2)));
fs.writeFileSync("exec-args.json", JSON.stringify(process.execArgv));
fs.writeFileSync("env.txt", process.env.WEAVIE_HOOK_PIPE || "");
function send(value) {
  process.stdout.write(JSON.stringify(value) + "\n");
}
readline.createInterface({ input: process.stdin }).on("line", line => {
  const message = JSON.parse(line);
  if (message.method === "initialize") {
    send({ id: message.id, result: { userAgent: "fake-codex" } });
  } else if (message.method === "thread/start" || message.method === "thread/resume") {
    send({ id: message.id, result: { thread: { id: "thread_fake" } } });
  } else if (message.method === "turn/start") {
    send({ id: message.id, result: { turn: { id: "turn_fake" } } });
    send({ method: "turn/started", params: { threadId: "thread_fake", turn: { id: "turn_fake", status: "running" } } });
    send({ method: "item/agentMessage/delta", params: { threadId: "thread_fake", turnId: "turn_fake", itemId: "item_fake", delta: "hello" } });
    send({ method: "turn/completed", params: { threadId: "thread_fake", turn: { id: "turn_fake", status: "completed" } } });
  } else if (message.method === "turn/interrupt") {
    send({ id: message.id, result: { turn: { id: message.params.turnId, status: "interrupted" } } });
    send({ method: "turn/interrupted", params: { threadId: "thread_fake", turn: { id: message.params.turnId, status: "interrupted" } } });
  } else if (message.method === "test/string-request") {
    send({ id: message.id, result: { ok: true } });
    send({ id: "approval-1", method: "item/commandExecution/requestApproval", params: { threadId: "thread_fake", turnId: "turn_fake", itemId: "item_fake", startedAtMs: 1 } });
  } else if (message.id === "approval-1") {
    fs.writeFileSync("string-response.json", JSON.stringify(message));
  } else if (message.method === "test/error-request") {
    send({ id: message.id, result: { ok: true } });
    send({ id: "unsupported-1", method: "item/tool/call", params: { threadId: "thread_fake", turnId: "turn_fake", itemId: "item_fake" } });
  } else if (message.id === "unsupported-1") {
    fs.writeFileSync("error-response.json", JSON.stringify(message));
  } else if (message.method === "test/exit") {
    process.exit(7);
  }
});
""";
}
