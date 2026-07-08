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

public sealed partial class CodexAppServerSessionTests : IDisposable {
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
	public async Task Start_EmitsHookIntegrationStartupMessages() {
		var events = new CapturingAgentEventSink();
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSessionWithHooks(events, messages, new StartupMessageCodexHookIntegration());

		session.Start();
		await WaitForAsync(() => messages.Any(message => message.Type == "warning"));

		var warning = Assert.Single(messages, message => message.Type == "warning");
		Assert.Equal("codex", warning.ProviderId);
		Assert.Equal("hook trust warning", warning.Text);
	}

	[Fact]
	public async Task Start_DoesNotSurfaceInternalLifecycleCards() {
		var events = new CapturingAgentEventSink();
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(events, messages);

		session.Start();
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "thread-start.json")));

		Assert.DoesNotContain(messages, message => message.Type == "process-started");
		Assert.DoesNotContain(messages, message => message.Type == "thread-ready");
	}

	[Fact]
	public async Task ThreadResume_SendsWeavieDeveloperInstructions() {
		var events = new CapturingAgentEventSink();
		List<AgentPaneMessage> messages = [];
		InMemoryFileSystem fileSystem = new();
		CodexThreadStore threads = new(fileSystem, "/codex-threads.json");
		threads.Adopt(_dir, "thread_saved");
		await using var session = CreateSessionWithThreads(events, messages, threads, fileSystem);

		session.Start();
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "thread-resume.json")));

		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "thread-resume.json")));
		string instructions = doc.RootElement.GetProperty("params").GetProperty("developerInstructions").GetString() ?? "";
		Assert.Equal("thread_saved", doc.RootElement.GetProperty("params").GetProperty("threadId").GetString());
		Assert.Contains("mcp__weavie__currentSession", instructions, StringComparison.Ordinal);
		Assert.Contains("local (loopback only)", instructions, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ThreadId_PersistsOnlyAfterTurnStarts() {
		InMemoryFileSystem fileSystem = new();
		CodexThreadStore threads = new(fileSystem, "/codex-threads.json");
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSessionWithThreads(new NullAgentEventSink(), messages, threads, fileSystem);

		session.Start();
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "thread-start.json")));

		Assert.False(threads.Resolve(_dir).Resume);

		session.SubmitPrompt("hi");
		await WaitForAsync(() => threads.Resolve(_dir).Resume);

		Assert.Equal("thread_fake", threads.Resolve(_dir).ThreadId);
	}

	[Fact]
	public async Task Start_WithUntrustedNonWeavieHook_SurfacesErrorAndDoesNotStartThread() {
		File.WriteAllText(Path.Combine(_dir, "unsafe-hooks"), "1");
		var events = new CapturingAgentEventSink();
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(events, messages);

		session.Start();
		await WaitForAsync(() => messages.Any(message => message.Type == "error"));

		var error = Assert.Single(messages, message => message.Type == "error");
		Assert.Contains("hook-trust bypass", error.Text, StringComparison.Ordinal);
		Assert.False(File.Exists(Path.Combine(_dir, "thread-start.json")));
	}

	[Fact]
	public async Task ApprovalRequest_UpdatesSharedStatusEvents() {
		var events = new CapturingAgentEventSink();
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(events, messages);

		session.Start();
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "thread-start.json")));

		session.SubmitPrompt("approval");
		await WaitForAsync(() => messages.Any(message => message.Type == "approval-requested"));

		Assert.Contains(events.Values, value => value is AgentPermissionRequested);

		session.ResolveApproval("approval-1", "accept");
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "approval-response.json")));

		Assert.Contains(events.Values, value => value is AgentPermissionResolved { RequiresUserInput: false });
		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "approval-response.json")));
		Assert.Equal("approval-1", doc.RootElement.GetProperty("id").GetString());
		Assert.Equal("accept", doc.RootElement.GetProperty("result").GetProperty("decision").GetString());

		int errorCount = messages.Count(message => message.Type == "error");
		session.ResolveApproval("approval-1", "accept");

		Assert.Equal(errorCount, messages.Count(message => message.Type == "error"));
	}

	[Fact]
	public async Task ThreadStart_SendsWeavieDeveloperInstructions() {
		var events = new CapturingAgentEventSink();
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(events, messages);

		session.Start();
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "thread-start.json")));

		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "thread-start.json")));
		string instructions = doc.RootElement.GetProperty("params").GetProperty("developerInstructions").GetString()!;
		Assert.Contains("mcp__weavie__currentSession", instructions, StringComparison.Ordinal);
		Assert.Contains("local (loopback only)", instructions, StringComparison.Ordinal);
	}

	[Fact]
	public async Task UnsupportedServerRequest_SurfacesErrorCard() {
		var events = new CapturingAgentEventSink();
		List<AgentPaneMessage> messages = [];
		await using var session = CreateSession(events, messages);

		session.Start();
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "thread-start.json")));

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
		await WaitForAsync(() => File.Exists(Path.Combine(_dir, "thread-start.json")));

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
		await WaitForAsync(() =>
			messages.Any(message => message.Type == "user-message" && message.Text == "describe it")
			&& messages.Any(message => message.Type == "user-image" && message.Status == "submitted"));
		Assert.Contains(messages, message => message.Type == "user-image" && message.Status == "attached");
		Assert.Contains(messages, message => message.Type == "user-message" && message.Text == "describe it");
		Assert.Contains(messages, message => message.Type == "user-image" && message.Status == "submitted");
	}

	private CodexAppServerSession CreateSession(IAgentEventSink events, List<AgentPaneMessage> messages) {
		InMemoryFileSystem fileSystem = new();
		return CreateSessionWithThreads(events, messages, new CodexThreadStore(fileSystem, "/codex-threads.json"), fileSystem);
	}

	private CodexAppServerSession CreateSessionWithHooks(
		IAgentEventSink events,
		List<AgentPaneMessage> messages,
		ICodexHookIntegration hooks) {
		InMemoryFileSystem fileSystem = new();
		return CreateSessionWithThreadsAndHooks(
			events, messages, new CodexThreadStore(fileSystem, "/codex-threads.json"), fileSystem, hooks);
	}

	private CodexAppServerSession CreateSessionWithThreads(
		IAgentEventSink events,
		List<AgentPaneMessage> messages,
		CodexThreadStore threads,
		InMemoryFileSystem fileSystem) =>
		CreateSessionWithThreadsAndHooks(events, messages, threads, fileSystem, NoopCodexHookIntegration.Instance);

	private CodexAppServerSession CreateSessionWithThreadsAndHooks(
		IAgentEventSink events,
		List<AgentPaneMessage> messages,
		CodexThreadStore threads,
		InMemoryFileSystem fileSystem,
		ICodexHookIntegration hooks) {
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
		}, threads, "node", hooks);
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

		public IReadOnlyList<AgentPaneMessage> StartupMessages => [];

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class StartupMessageCodexHookIntegration : ICodexHookIntegration {
		public IReadOnlyList<string> GlobalArguments => [];

		public IReadOnlyList<string> AppServerArguments => [];

		public IReadOnlyDictionary<string, string> Environment { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

		public IReadOnlyList<AgentPaneMessage> StartupMessages => [
			new AgentPaneMessage {
				Type = "warning",
				ProviderId = "codex",
				Status = "warning",
				Text = "hook trust warning",
			},
		];

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

}
