using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Layout;
using Weavie.Core.Mcp;
using Weavie.Core.Sessions;
using Weavie.Core.Theming;
using Weavie.Hosting.Agents;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AgentSessionHostTests {
	[Fact]
	public async Task StructuredProvider_DoesNotStartUntilSlotIsKnown() {
		string slot = string.Empty;
		await using var fixture = CreateFixture(() => slot, static (_, _) => { });
		var (bridge, session, host) = (fixture.Bridge, fixture.Session, fixture.Host);

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

		var replayed = Assert.Single(bridge.PostedOfType("agent-pane"), value =>
			value.GetProperty("message").GetProperty("itemId").GetString() == "item-1");
		Assert.Equal("hello world", replayed.GetProperty("message").GetProperty("text").GetString());

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

		string?[] shared = [.. bridge.PostedOfType("agent-pane")
			.Where(value => value.GetProperty("message").GetProperty("itemId").GetString() == "item-shared")
			.Select(value => value.GetProperty("message").GetProperty("text").GetString())];
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

		string?[] collisionTexts = [.. bridge.PostedOfType("agent-pane")
			.Where(value => value.GetProperty("message").GetProperty("itemId").GetString() == "item-collision")
			.Select(value => value.GetProperty("message").GetProperty("text").GetString())];
		Assert.Equal(collisions.Select(collision => collision.Text), collisionTexts);
	}

	[Fact]
	public async Task StructuredProvider_SeedsPersistedTranscript_BeforeStart() {
		// A prior session's persisted result — the durable transcript on disk before this session is built.
		await using var fixture = CreateFixture(
			static () => "slot-1",
			static (fileSystem, transcriptPath) => new AgentPaneTranscriptStore(fileSystem, transcriptPath).Append(
				Completed("item-1", "prior result")));

		// The provider hasn't started (no thread/resume, no hydration): a reconnecting page's ReplayPane still
		// restores the prior result — from the synchronous disk seed. This is the reopen-reconnect fix.
		Assert.False(fixture.Session.Started);
		fixture.Host.ReplayPane();

		var replayed = Assert.Single(fixture.Bridge.PostedOfType("agent-pane"));
		Assert.Equal("item-completed", replayed.GetProperty("message").GetProperty("type").GetString());
		Assert.Equal("prior result", replayed.GetProperty("message").GetProperty("text").GetString());
	}

	// Regression for the remote cold-start blank pane: on a slow resume, a page's ReplayPane (page `ready`, one
	// thread) races the async hydrate (thread/resume, another thread). Both reset then repopulate the pane. Unless
	// their web posts are ordered with their `_paneMessages` mutations, a trailing ReplayPane reset can land after
	// hydrate delivered its content and wipe the pane. The pane must always converge to the authoritative
	// transcript, whichever way the two interleave.
	[Fact]
	public async Task ReplayPane_RacingHydrate_ConvergesToHydratedTranscript() {
		await using var fixture = CreateFixture(static () => "slot-1", static (_, _) => { });
		var (bridge, session, host) = (fixture.Bridge, fixture.Session, fixture.Host);

		// A resumed thread re-emits transcript-reset + its completed items; this is the authoritative end state.
		AgentPaneMessage[] hydrated = [Completed("fresh-0", "fresh a"), Completed("fresh-1", "fresh b")];

		for (int iteration = 0; iteration < 40; iteration++) {
			// Restore the "large disk seed already present" baseline before each race — the wide seed makes
			// ReplayPane's post loop long enough to reliably expose an unordered trailing reset.
			session.Emit(new AgentPaneMessage { Type = "transcript-reset", ProviderId = "codex" });
			for (int i = 0; i < 100; i++) {
				session.Emit(Completed($"seed-{i}", $"seed {i}"));
			}

			bridge.Clear();
			Exception? threadError = null;
			var barrier = new Barrier(2);
			var hydrate = new Thread(() => {
				try {
					barrier.SignalAndWait();
					session.Emit(new AgentPaneMessage { Type = "transcript-reset", ProviderId = "codex" });
					foreach (var message in hydrated) {
						session.Emit(message);
					}
				} catch (Exception ex) {
					threadError = ex;
				}
			});
			var replay = new Thread(() => {
				try {
					barrier.SignalAndWait();
					host.ReplayPane();
				} catch (Exception ex) {
					threadError = ex;
				}
			});
			hydrate.Start();
			replay.Start();
			hydrate.Join();
			replay.Join();

			Assert.Null(threadError);
			Assert.Equal(hydrated.Select(message => message.ItemId), VisibleItemIds(bridge));
		}
	}

	// The item ids the page would render, reconstructed from the posts in order: a reset clears the pane, an
	// agent-pane message appends its item (keyed, so a repeat updates in place) — mirroring AgentPaneAccumulator.
	private static IReadOnlyList<string> VisibleItemIds(FakeHostBridge bridge) {
		var order = new List<string>();
		var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (string json in bridge.Posted) {
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;
			string? type = root.GetProperty("type").GetString();
			if (type == "agent-pane-reset") {
				order.Clear();
				indexes.Clear();
			} else if (type == "agent-pane"
				&& root.GetProperty("message").TryGetProperty("itemId", out var id)
				&& id.ValueKind == JsonValueKind.String
				&& id.GetString() is { } itemId
				&& !indexes.ContainsKey(itemId)) {
				indexes[itemId] = order.Count;
				order.Add(itemId);
			}
		}

		return order;
	}

	private static AgentPaneMessage Completed(string itemId, string text) => new() {
		Type = "item-completed",
		ProviderId = "codex",
		TurnId = "turn",
		ItemId = itemId,
		Text = text,
		Status = "completed",
	};

	private static HostFixture CreateFixture(Func<string> slot, Action<IFileSystem, string> seedTranscript) {
		string dir = Path.Combine(Path.GetTempPath(), "weavie-agent-host-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		var fileSystem = new InMemoryFileSystem();
		string transcriptPath = Path.Combine(dir, "agent-pane.json");
		seedTranscript(fileSystem, transcriptPath);
		var settings = CoreSettings.CreateStore(Path.Combine(dir, "settings.toml"), enableWatcher: false);
		var commandRegistry = CoreCommands.CreateRegistry();
		var bridge = new FakeHostBridge();
		var registry = new CapabilityRegistryHost(
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
			slot);
		var session = new FakeStructuredSession();
		var host = new AgentSessionHost(
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
				CurrentSessionId = slot,
			},
			bridge,
			settings,
			new NoopPtyLauncher(),
			transcriptPath);
		return new HostFixture(bridge, session, host, registry, settings);
	}

	private sealed class HostFixture(
		FakeHostBridge bridge,
		FakeStructuredSession session,
		AgentSessionHost host,
		CapabilityRegistryHost registry,
		SettingsStore settings) : IAsyncDisposable {
		public FakeHostBridge Bridge => bridge;

		public FakeStructuredSession Session => session;

		public AgentSessionHost Host => host;

		public async ValueTask DisposeAsync() {
			await host.DisposeAsync();
			await registry.DisposeAsync();
			settings.Dispose();
		}
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
