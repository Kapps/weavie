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
using Weavie.Hosting.Agents;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AgentSessionHostTests {
	[Fact]
	public async Task StructuredProvider_DoesNotStartUntilSlotIsKnown() {
		string dir = Path.Combine(Path.GetTempPath(), "weavie-agent-host-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		string slot = string.Empty;
		var fileSystem = new InMemoryFileSystem();
		using var settings = CoreSettings.CreateStore(Path.Combine(dir, "settings.toml"), enableWatcher: false);
		settings.Set(AgentSettings.PaneCoalesceMs, JsonSerializer.SerializeToElement(0L)); // assert live frames synchronously
		var commandRegistry = CoreCommands.CreateRegistry();
		var bridge = new FakeHostBridge();
		await using var registry = new CapabilityRegistryHost(
			AgentSessionCredential.Create(),
			FakeDiffPresenter.AlwaysKeep(),
			[dir],
			"weavie",
			settings,
			new LayoutStore(fileSystem, LayoutPanes.CreateRegistry(), "/layout.json"),
			new EditorStore(),
			exposeIdeTools: true,
			new CommandDispatcher(commandRegistry),
			new KeybindingStore(commandRegistry, Path.Combine(dir, "keybindings.json"), enableWatcher: false),
			new ThemeOverridesStore(fileSystem, "/theme-overrides.json"),
			() => slot);
		var session = new FakeStructuredSession();
		await using var host = new AgentSessionHost(
			new FakeStructuredProvider(session),
			new AgentSessionContext {
				Settings = settings,
				Workspace = dir,
				FileSystem = fileSystem,
				Registry = registry,
				DiffPresenter = FakeDiffPresenter.AlwaysKeep(),
				Editor = new EditorStore(),
				Runtime = new HostRuntimeInfo(HostTransport.Local, Managed: false, "test"),
				Events = new NullAgentEventSink(),
				CurrentSessionId = () => slot,
			},
			bridge,
			settings,
			new NoopPtyLauncher(),
			Path.Combine(dir, "agent-pane.json"));

		Assert.False(session.Started);
		slot = "slot-1";
		host.Structured!.Start();

		var message = Assert.Single(bridge.PostedOfType("agent-pane"));
		Assert.Equal("slot-1", message.GetProperty("slot").GetString());
		Assert.Equal("started", message.GetProperty("message").GetProperty("type").GetString());

		session.Emit(new AgentPaneMessage {
			Type = "agent-message-delta",
			ProviderId = "codex",
			TurnId = "turn-1",
			ItemId = "item-1",
			Text = "hello ",
		});
		session.Emit(new AgentPaneMessage {
			Type = "agent-message-delta",
			ProviderId = "codex",
			TurnId = "turn-1",
			ItemId = "item-1",
			Text = "world",
		});
		bridge.Clear();
		host.ReplayPane();

		var replayed = Assert.Single(Replayed(bridge), value =>
			value.GetProperty("itemId").GetString() == "item-1");
		Assert.Equal("hello world", replayed.GetProperty("text").GetString());

		session.Emit(new AgentPaneMessage {
			Type = "agent-message-delta",
			ProviderId = "codex",
			ThreadId = "thread-a",
			TurnId = "turn-shared",
			ItemId = "item-shared",
			Text = "alpha",
		});
		session.Emit(new AgentPaneMessage {
			Type = "agent-message-delta",
			ProviderId = "codex",
			ThreadId = "thread-b",
			TurnId = "turn-shared",
			ItemId = "item-shared",
			Text = "beta",
		});
		bridge.Clear();
		host.ReplayPane();

		string?[] shared = [.. Replayed(bridge)
			.Where(value => value.GetProperty("itemId").GetString() == "item-shared")
			.Select(value => value.GetProperty("text").GetString())];
		Assert.Collection(
			shared,
			value => Assert.Equal("alpha", value),
			value => Assert.Equal("beta", value));

		(string? Thread, string? Turn, string Text)[] collisions = [
			(null, "session", "missing-thread"),
			("thread", null, "missing-turn"),
			("a:b", "c", "thread-delimiter"),
			("a", "b:c", "turn-delimiter"),
		];
		foreach (var collision in collisions) {
			session.Emit(new AgentPaneMessage {
				Type = "agent-message-delta",
				ProviderId = "codex",
				ThreadId = collision.Thread,
				TurnId = collision.Turn,
				ItemId = "item-collision",
				Text = collision.Text,
			});
		}
		bridge.Clear();
		host.ReplayPane();

		string?[] collisionTexts = [.. Replayed(bridge)
			.Where(value => value.GetProperty("itemId").GetString() == "item-collision")
			.Select(value => value.GetProperty("text").GetString())];
		Assert.Equal(collisions.Select(collision => collision.Text), collisionTexts);
	}

	[Fact]
	public async Task ReplayPane_batches_the_whole_transcript_into_one_frame() {
		string dir = Path.Combine(Path.GetTempPath(), "weavie-agent-host-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		var fileSystem = new InMemoryFileSystem();
		using var settings = CoreSettings.CreateStore(Path.Combine(dir, "settings.toml"), enableWatcher: false);
		settings.Set(AgentSettings.PaneCoalesceMs, JsonSerializer.SerializeToElement(0L)); // assert live frames synchronously
		var commandRegistry = CoreCommands.CreateRegistry();
		var bridge = new FakeHostBridge();
		await using var registry = new CapabilityRegistryHost(
			AgentSessionCredential.Create(),
			FakeDiffPresenter.AlwaysKeep(),
			[dir],
			"weavie",
			settings,
			new LayoutStore(fileSystem, LayoutPanes.CreateRegistry(), "/layout.json"),
			new EditorStore(),
			exposeIdeTools: true,
			new CommandDispatcher(commandRegistry),
			new KeybindingStore(commandRegistry, Path.Combine(dir, "keybindings.json"), enableWatcher: false),
			new ThemeOverridesStore(fileSystem, "/theme-overrides.json"),
			() => "slot-1");
		var session = new FakeStructuredSession();
		await using var host = new AgentSessionHost(
			new FakeStructuredProvider(session),
			new AgentSessionContext {
				Settings = settings,
				Workspace = dir,
				FileSystem = fileSystem,
				Registry = registry,
				DiffPresenter = FakeDiffPresenter.AlwaysKeep(),
				Editor = new EditorStore(),
				Runtime = new HostRuntimeInfo(HostTransport.Local, Managed: false, "test"),
				Events = new NullAgentEventSink(),
				CurrentSessionId = () => "slot-1",
			},
			bridge,
			settings,
			new NoopPtyLauncher(),
			Path.Combine(dir, "agent-pane.json"));

		host.Structured!.Start();
		// A long transcript — far past the bridge's 512-deep outbox. Before batching, ReplayPane posted one frame
		// per entry, bursting past the outbox on a slow (remote) link and getting the healthy page dropped.
		for (int i = 0; i < 1000; i++) {
			session.Emit(new AgentPaneMessage {
				Type = "item-completed",
				ProviderId = "codex",
				TurnId = $"turn-{i}",
				ItemId = $"item-{i}",
				Text = $"line {i}",
				Status = "completed",
			});
		}
		bridge.Clear();
		host.ReplayPane();

		// The whole replay is a reset plus a single batch frame — a bounded burst no matter how long the transcript.
		Assert.Equal(2, bridge.Posted.Count);
		Assert.Single(bridge.PostedOfType("agent-pane-reset"));
		var batch = Assert.Single(bridge.PostedOfType("agent-pane-batch"));
		Assert.Equal(1001, batch.GetProperty("messages").GetArrayLength()); // 1000 items + the "started" marker
	}

	[Fact]
	public async Task LiveMessages_within_the_window_coalesce_into_one_batch_frame() {
		string dir = Path.Combine(Path.GetTempPath(), "weavie-agent-host-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		var fileSystem = new InMemoryFileSystem();
		using var settings = CoreSettings.CreateStore(Path.Combine(dir, "settings.toml"), enableWatcher: false);
		settings.Set(AgentSettings.PaneCoalesceMs, JsonSerializer.SerializeToElement(200L)); // batch a burst within 200ms
		var commandRegistry = CoreCommands.CreateRegistry();
		var bridge = new FakeHostBridge();
		await using var registry = new CapabilityRegistryHost(
			AgentSessionCredential.Create(),
			FakeDiffPresenter.AlwaysKeep(),
			[dir],
			"weavie",
			settings,
			new LayoutStore(fileSystem, LayoutPanes.CreateRegistry(), "/layout.json"),
			new EditorStore(),
			exposeIdeTools: true,
			new CommandDispatcher(commandRegistry),
			new KeybindingStore(commandRegistry, Path.Combine(dir, "keybindings.json"), enableWatcher: false),
			new ThemeOverridesStore(fileSystem, "/theme-overrides.json"),
			() => "slot-1");
		var session = new FakeStructuredSession();
		await using var host = new AgentSessionHost(
			new FakeStructuredProvider(session),
			new AgentSessionContext {
				Settings = settings,
				Workspace = dir,
				FileSystem = fileSystem,
				Registry = registry,
				DiffPresenter = FakeDiffPresenter.AlwaysKeep(),
				Editor = new EditorStore(),
				Runtime = new HostRuntimeInfo(HostTransport.Local, Managed: false, "test"),
				Events = new NullAgentEventSink(),
				CurrentSessionId = () => "slot-1",
			},
			bridge,
			settings,
			new NoopPtyLauncher(),
			Path.Combine(dir, "agent-pane.json"));

		host.Structured!.Start(); // "started"
		for (int i = 0; i < 5; i++) {
			session.Emit(new AgentPaneMessage {
				Type = "item-completed",
				ProviderId = "codex",
				TurnId = $"turn-{i}",
				ItemId = $"item-{i}",
				Text = $"line {i}",
				Status = "completed",
			});
		}

		// The 6 messages (started + 5) all land inside one window, so the flush is a single batch frame — no
		// per-message agent-pane frame escapes, which is what keeps a fast turn from flooding the outbox.
		int count = await Wait.ForAsync(() =>
			bridge.PostedOfType("agent-pane-batch").Count is var c and > 0 ? c : (int?)null);
		Assert.Equal(1, count);
		Assert.Empty(bridge.PostedOfType("agent-pane"));
		Assert.Equal(6, Replayed(bridge).Count);
	}

	// The messages carried by the single agent-pane-batch frame the bridge received (a replay or a live flush).
	private static IReadOnlyList<JsonElement> Replayed(FakeHostBridge bridge) {
		var batch = Assert.Single(bridge.PostedOfType("agent-pane-batch"));
		return [.. batch.GetProperty("messages").EnumerateArray()];
	}

	[Fact]
	public async Task StructuredProvider_SeedsPersistedTranscript_BeforeStart() {
		string dir = Path.Combine(Path.GetTempPath(), "weavie-agent-host-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		var fileSystem = new InMemoryFileSystem();
		string transcriptPath = Path.Combine(dir, "agent-pane.json");
		// A prior session's persisted result — the durable transcript on disk before this session is built.
		new AgentPaneTranscriptStore(fileSystem, transcriptPath).Append(new AgentPaneMessage {
			Type = "item-completed",
			ProviderId = "codex",
			TurnId = "turn-1",
			ItemId = "item-1",
			Text = "prior result",
			Status = "completed",
		});

		using var settings = CoreSettings.CreateStore(Path.Combine(dir, "settings.toml"), enableWatcher: false);
		settings.Set(AgentSettings.PaneCoalesceMs, JsonSerializer.SerializeToElement(0L)); // assert live frames synchronously
		var commandRegistry = CoreCommands.CreateRegistry();
		var bridge = new FakeHostBridge();
		await using var registry = new CapabilityRegistryHost(
			AgentSessionCredential.Create(),
			FakeDiffPresenter.AlwaysKeep(),
			[dir],
			"weavie",
			settings,
			new LayoutStore(fileSystem, LayoutPanes.CreateRegistry(), "/layout.json"),
			new EditorStore(),
			exposeIdeTools: true,
			new CommandDispatcher(commandRegistry),
			new KeybindingStore(commandRegistry, Path.Combine(dir, "keybindings.json"), enableWatcher: false),
			new ThemeOverridesStore(fileSystem, "/theme-overrides.json"),
			() => "slot-1");
		var session = new FakeStructuredSession();
		await using var host = new AgentSessionHost(
			new FakeStructuredProvider(session),
			new AgentSessionContext {
				Settings = settings,
				Workspace = dir,
				FileSystem = fileSystem,
				Registry = registry,
				DiffPresenter = FakeDiffPresenter.AlwaysKeep(),
				Editor = new EditorStore(),
				Runtime = new HostRuntimeInfo(HostTransport.Local, Managed: false, "test"),
				Events = new NullAgentEventSink(),
				CurrentSessionId = () => "slot-1",
			},
			bridge,
			settings,
			new NoopPtyLauncher(),
			transcriptPath);

		// The provider hasn't started (no thread/resume, no hydration): a reconnecting page's ReplayPane still
		// restores the prior result — from the synchronous disk seed. This is the reopen-reconnect fix.
		Assert.False(session.Started);
		host.ReplayPane();

		var replayed = Assert.Single(Replayed(bridge));
		Assert.Equal("item-completed", replayed.GetProperty("type").GetString());
		Assert.Equal("prior result", replayed.GetProperty("text").GetString());
	}

	private sealed class FakeStructuredProvider(FakeStructuredSession session) : IAgentProvider {
		public AgentProviderInfo Info { get; } = new() {
			Id = "codex",
			Name = "Codex",
			Capabilities = AgentProviderCapabilities.StructuredPane,
			Available = true,
		};

		public IAgentSession CreateSession(AgentSessionContext context) => session;
	}

	private sealed class FakeStructuredSession : IStructuredAgentSession {
		public event Action<AgentPaneMessage>? PaneMessage;

		public bool Started { get; private set; }

		public void Start() {
			Started = true;
			PaneMessage?.Invoke(new AgentPaneMessage { Type = "started", ProviderId = "codex" });
		}

		public void Emit(AgentPaneMessage message) => PaneMessage?.Invoke(message);

		public void Submit(AgentTurnSubmission submission) => throw new NotSupportedException();

		public void PrefillPrompt(string prompt) => throw new NotSupportedException();

		public void Interrupt() => throw new NotSupportedException();

		public void Restart() => throw new NotSupportedException();

		public void ResolveApproval(string requestId, string decision) => throw new NotSupportedException();

		public void ResolveInput(string requestId, IReadOnlyDictionary<string, IReadOnlyList<string>> answers) =>
			throw new NotSupportedException();

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class NullAgentEventSink : IAgentEventSink {
		public AgentEventFeedback Observe(AgentEvent value) => AgentEventFeedback.None;
	}
}
