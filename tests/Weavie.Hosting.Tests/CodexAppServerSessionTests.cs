using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Layout;
using Weavie.Core.Mcp;
using Weavie.Core.Sessions;
using Weavie.Core.Shell;
using Weavie.Core.Theming;
using Weavie.Hosting.Agents.Codex;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class CodexAppServerSessionTests : IDisposable {
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "weavie-codex-session-tests", Guid.NewGuid().ToString("N"));

	public CodexAppServerSessionTests() {
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
	public async Task ThreadId_PersistsOnlyAfterTurnStarts() {
		InMemoryFileSystem fileSystem = new();
		using var settings = CoreSettings.CreateStore(Path.Combine(_dir, "settings.toml"), enableWatcher: false);
		var commandRegistry = CoreCommands.CreateRegistry();
		CodexThreadStore threads = new(fileSystem, "/codex-threads.json");
		List<AgentPaneMessage> messages = [];
		CapabilityRegistryHost registry = new(
			AgentSessionCredential.Create(),
			FakeDiffPresenter.AlwaysKeep(),
			[_dir],
			"weavie",
			settings,
			new LayoutStore(fileSystem, LayoutPanes.CreateRegistry(), "/layout.json"),
			new EditorStore(),
			exposeIdeTools: true,
			new CommandDispatcher(commandRegistry),
			new KeybindingStore(commandRegistry, Path.Combine(_dir, "keybindings.json"), enableWatcher: false),
			new ThemeOverridesStore(fileSystem, "/theme-overrides.json"),
			() => "slot-1");
		await using CodexAppServerSession session = new(new AgentSessionContext {
			Settings = settings,
			Workspace = _dir,
			FileSystem = fileSystem,
			Registry = registry,
			DiffPresenter = FakeDiffPresenter.AlwaysKeep(),
			Editor = new EditorStore(),
			Runtime = new HostRuntimeInfo(HostTransport.Local, Managed: false, "test"),
			Events = new NullAgentEventSink(),
			CurrentSessionId = () => "slot-1",
		}, threads, "node", NoopCodexHookIntegration.Instance);
		session.PaneMessage += messages.Add;

		session.Start();
		await WaitForAsync(() => messages.Any(message => message.Type == "thread-ready"));

		Assert.False(threads.Resolve(_dir).Resume);

		session.SubmitPrompt("hi");
		await WaitForAsync(() => threads.Resolve(_dir).Resume);

		Assert.Equal("thread_fake", threads.Resolve(_dir).ThreadId);
	}

	[Fact]
	public async Task ApprovalRequest_UpdatesSharedStatusEvents() {
		var events = new CapturingAgentEventSink();
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(events, messages);

		session.Start();
		await WaitForAsync(() => messages.Any(message => message.Type == "thread-ready"));

		session.SubmitPrompt("approval");
		await WaitForAsync(() => messages.Any(message => message.Type == "approval-requested"));

		Assert.Contains(events.Values, value => value is AgentPermissionRequested);

		session.ResolveApproval("approval-1", "accept");
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "approval-response.json")));

		Assert.Contains(events.Values, value => value is AgentPermissionResolved { RequiresUserInput: false });
		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "approval-response.json")));
		Assert.Equal("approval-1", doc.RootElement.GetProperty("id").GetString());
		Assert.Equal("accept", doc.RootElement.GetProperty("result").GetProperty("decision").GetString());
	}

	[Fact]
	public async Task UnsupportedServerRequest_SurfacesErrorCard() {
		var events = new CapturingAgentEventSink();
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(events, messages);

		session.Start();
		await WaitForAsync(() => messages.Any(message => message.Type == "thread-ready"));

		session.SubmitPrompt("unsupported");
		await WaitForAsync(() => messages.Any(message => message.ItemType == "item/tool/call"));

		var error = Assert.Single(messages, message => message.ItemType == "item/tool/call");
		Assert.Equal("error", error.Type);
		Assert.Contains("not supported", error.Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AttachImage_SendsLocalImageWithNextPrompt() {
		var events = new CapturingAgentEventSink();
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(events, messages);

		session.Start();
		await WaitForAsync(() => messages.Any(message => message.Type == "thread-ready"));

		session.AttachImage(Path.Combine(_dir, "paste-1.png"));
		Assert.False(File.Exists(Path.Combine(_dir, "image-turn.json")));
		session.SubmitPrompt("describe it");
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "image-turn.json")));

		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "image-turn.json")));
		var input = doc.RootElement.GetProperty("params").GetProperty("input");
		Assert.Equal("text", input[0].GetProperty("type").GetString());
		Assert.Equal("describe it", input[0].GetProperty("text").GetString());
		Assert.Equal("localImage", input[1].GetProperty("type").GetString());
		Assert.Equal(Path.Combine(_dir, "paste-1.png"), input[1].GetProperty("path").GetString());
		Assert.Contains(messages, message => message.Type == "user-image" && message.Status == "attached");
		Assert.Contains(messages, message => message.Type == "user-message" && message.Text == "describe it");
		Assert.Contains(messages, message => message.Type == "user-image" && message.Status == "submitted");
	}

	private CodexAppServerSession CreateSession(IAgentEventSink events, List<AgentPaneMessage> messages) {
		InMemoryFileSystem fileSystem = new();
		var settings = CoreSettings.CreateStore(Path.Combine(_dir, "settings.toml"), enableWatcher: false);
		var commandRegistry = CoreCommands.CreateRegistry();
		CapabilityRegistryHost registry = new(
			AgentSessionCredential.Create(),
			FakeDiffPresenter.AlwaysKeep(),
			[_dir],
			"weavie",
			settings,
			new LayoutStore(fileSystem, LayoutPanes.CreateRegistry(), "/layout.json"),
			new EditorStore(),
			exposeIdeTools: true,
			new CommandDispatcher(commandRegistry),
			new KeybindingStore(commandRegistry, Path.Combine(_dir, "keybindings.json"), enableWatcher: false),
			new ThemeOverridesStore(fileSystem, "/theme-overrides.json"),
			() => "slot-1");
		CodexAppServerSession session = new(new AgentSessionContext {
			Settings = settings,
			Workspace = _dir,
			FileSystem = fileSystem,
			Registry = registry,
			DiffPresenter = FakeDiffPresenter.AlwaysKeep(),
			Editor = new EditorStore(),
			Runtime = new HostRuntimeInfo(HostTransport.Local, Managed: false, "test"),
			Events = events,
			CurrentSessionId = () => "slot-1",
		}, new CodexThreadStore(fileSystem, "/codex-threads.json"), "node", NoopCodexHookIntegration.Instance);
		session.PaneMessage += messages.Add;
		return session;
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

	private sealed class NullAgentEventSink : IAgentEventSink {
		public AgentEventFeedback Observe(AgentEvent value) => AgentEventFeedback.None;
	}

	private sealed class CapturingAgentEventSink : IAgentEventSink {
		public List<AgentEvent> Values { get; } = [];

		public AgentEventFeedback Observe(AgentEvent value) {
			Values.Add(value);
			return AgentEventFeedback.None;
		}
	}

	private sealed class NoopCodexHookIntegration : ICodexHookIntegration {
		public static NoopCodexHookIntegration Instance { get; } = new();

		public IReadOnlyList<string> GlobalArguments => [];

		public IReadOnlyList<string> AppServerArguments => [];

		public IReadOnlyDictionary<string, string> Environment { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

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
  } else if (message.method === "thread/start") {
    send({ id: message.id, result: { thread: { id: "thread_fake" } } });
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
