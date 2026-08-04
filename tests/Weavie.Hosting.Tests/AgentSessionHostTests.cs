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
using Weavie.Hosting.Messaging;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AgentSessionHostTests {
	[Fact]
	public async Task StructuredProvider_DoesNotStartUntilSlotIsKnown() {
		string slot = string.Empty;
		// Window 0 so each live message posts its own agent-pane frame, asserted synchronously below.
		await using var fixture = CreateFixture(() => slot, static (_, _) => { }, 0);
		var (bridge, session, host) = (fixture.Bridge, fixture.Session, fixture.Host);

		Assert.False(session.Started);
		slot = "slot-1";
		host.Structured!.Start();
		await host.DrainPaneAsync(CancellationToken.None);

		var message = Assert.Single(bridge.PostedEventsNamed("pane"));
		Assert.Equal("started", message.GetProperty("type").GetString());

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
		await host.DrainPaneAsync(CancellationToken.None);
		bridge.Clear();
		host.ReplayPane();
		await host.DrainPaneAsync(CancellationToken.None);

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
		await host.DrainPaneAsync(CancellationToken.None);
		bridge.Clear();
		host.ReplayPane();
		await host.DrainPaneAsync(CancellationToken.None);

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
		await host.DrainPaneAsync(CancellationToken.None);
		bridge.Clear();
		host.ReplayPane();
		await host.DrainPaneAsync(CancellationToken.None);

		string?[] collisionTexts = [.. Replayed(bridge)
			.Where(value => value.GetProperty("itemId").GetString() == "item-collision")
			.Select(value => value.GetProperty("text").GetString())];
		Assert.Equal(collisions.Select(collision => collision.Text), collisionTexts);
	}

	[Fact]
	public async Task ReplayPane_replaces_the_whole_transcript_atomically() {
		await using var fixture = CreateFixture(static () => "slot-1", static (_, _) => { }, 0);
		var (bridge, session, host) = (fixture.Bridge, fixture.Session, fixture.Host);

		host.Structured!.Start();
		// A long transcript — far past the bridge's 512-deep outbox. Before batching, ReplayPane posted one frame
		// per entry, bursting past the outbox on a slow (remote) link and getting the healthy page dropped.
		for (int i = 0; i < 1000; i++) {
			session.Emit(Completed($"item-{i}", $"line {i}"));
		}
		await host.DrainPaneAsync(CancellationToken.None);
		bridge.Clear();
		host.ReplayPane();
		await host.DrainPaneAsync(CancellationToken.None);

		// A dropped transport cannot strand a destructive reset ahead of an interrupted transcript replay.
		var snapshot = Assert.Single(bridge.PostedEventsNamed("paneSnapshot"));
		Assert.Equal(1001, snapshot.GetProperty("messages").GetArrayLength()); // 1000 items + the "started" marker
	}

	[Fact]
	public async Task LiveMessages_within_the_window_coalesce_into_one_batch_frame() {
		await using var fixture = CreateFixture(static () => "slot-1", static (_, _) => { }, 200);
		var (bridge, session, host) = (fixture.Bridge, fixture.Session, fixture.Host);

		host.Structured!.Start(); // "started"
		for (int i = 0; i < 5; i++) {
			session.Emit(Completed($"item-{i}", $"line {i}"));
		}

		// The 6 messages (started + 5) all land inside one window, so the flush is a single batch frame — no
		// per-message agent-pane frame escapes, which is what keeps a fast turn from flooding the outbox.
		int count = await Wait.ForAsync(() =>
			bridge.PostedEventsNamed("paneBatch").Count is var c and > 0 ? c : (int?)null);
		Assert.Equal(1, count);
		Assert.Empty(bridge.PostedEventsNamed("pane"));
		Assert.Equal(6, Batched(bridge).Count);
	}

	[Fact]
	public async Task StructuredProvider_SeedsPersistedTranscript_BeforeStart() {
		// A prior session's persisted result — the durable transcript on disk before this session is built.
		await using var fixture = CreateFixture(
			static () => "slot-1",
			static (fileSystem, transcriptPath) =>
				new AgentPaneTranscriptStore(fileSystem, transcriptPath).Append(Completed("item-1", "prior result")),
			0);

		// The provider hasn't started (no thread/resume, no hydration): a reconnecting page's ReplayPane still
		// restores the prior result after the journal worker loads it. This is the reopen-reconnect fix.
		Assert.False(fixture.Session.Started);
		await fixture.Host.DrainPaneAsync(CancellationToken.None);
		fixture.Bridge.Clear();
		fixture.Host.ReplayPane();
		await fixture.Host.DrainPaneAsync(CancellationToken.None);

		var replayed = Assert.Single(Replayed(fixture.Bridge));
		Assert.Equal("item-completed", replayed.GetProperty("type").GetString());
		Assert.Equal("prior result", replayed.GetProperty("text").GetString());
	}

	[Fact]
	public async Task PersistedTranscriptReplaysWhenActivationBeatsJournalLoad() {
		string dir = Path.Combine(Path.GetTempPath(), "weavie-agent-host-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		string transcriptPath = Path.Combine(dir, "agent-pane.json");
		using var release = new ManualResetEventSlim();
		var fileSystem = new BlockingReadFileSystem(transcriptPath, release);
		await using var fixture = CreateFixture(
			static () => "slot-1",
			fileSystem,
			transcriptPath,
			0);

		try {
			fixture.Host.ReplayPane();
			await Wait.ForAsync(() =>
				fixture.Bridge.PostedEventsNamed("paneSnapshot").Count > 0 ? 1 : (int?)null);
			var initial = Assert.Single(fixture.Bridge.PostedEventsNamed("paneSnapshot"));
			Assert.Empty(initial.GetProperty("messages").EnumerateArray());
		} finally {
			release.Set();
		}

		await fixture.Host.WaitForPaneReadyAsync(CancellationToken.None);
		await fixture.Host.DrainPaneAsync(CancellationToken.None);
		var replay = fixture.Bridge.PostedEventsNamed("paneSnapshot")[^1];
		var message = Assert.Single(replay.GetProperty("messages").EnumerateArray());
		Assert.Equal("persisted after activation", message.GetProperty("text").GetString());
	}

	[Fact]
	public async Task CompletedPlan_IsAvailableOnlyForItsExactCurrentIdentity() {
		await using var fixture = CreateFixture(static () => "slot-1", static (_, _) => { }, 0);
		var (session, host) = (fixture.Session, fixture.Host);
		const string threadId = "thread-plan";
		const string turnId = "turn-plan";
		const string itemId = "item-plan";

		session.Emit(new AgentPaneMessage {
			Type = "plan-delta",
			ProviderId = "codex",
			ThreadId = threadId,
			TurnId = turnId,
			ItemId = itemId,
			ItemType = "plan",
			Text = "# Draft",
		});
		Assert.False(host.TryGetCompletedPlan(threadId, turnId, itemId, out _));

		session.Emit(new AgentPaneMessage {
			Type = "item-completed",
			ProviderId = "codex",
			ThreadId = threadId,
			TurnId = turnId,
			ItemId = itemId,
			ItemType = "plan",
			Text = "# Final plan",
		});
		Assert.True(host.TryGetCompletedPlan(threadId, turnId, itemId, out var plan));
		Assert.Equal("# Final plan", plan.Markdown);
		Assert.False(host.TryGetCompletedPlan("another-thread", turnId, itemId, out _));
		Assert.False(host.TryGetCompletedPlan(threadId, "another-turn", itemId, out _));
		Assert.False(host.TryGetCompletedPlan(threadId, turnId, "another-item", out _));

		session.Emit(new AgentPaneMessage { Type = "transcript-reset", ProviderId = "codex" });
		Assert.False(host.TryGetCompletedPlan(threadId, turnId, itemId, out _));
	}

	// Regression for the remote cold-start blank pane: on a slow resume, a page's ReplayPane (page `ready`, one
	// thread) races the async hydrate (thread/resume, another thread). Both reset then repopulate the pane. Unless
	// their web posts are ordered with their `_paneMessages` mutations, a trailing ReplayPane reset can land after
	// hydrate delivered its content and wipe the pane. The pane must always converge to the authoritative
	// transcript, whichever way the two interleave.
	//
	// The racers run on pool threads: per-iteration `new Thread` starts (80 across the loop) hit
	// pthread_create EAGAIN (surfaced as OutOfMemoryException) on contended CI runners.
	[Fact]
	public async Task ReplayPane_RacingHydrate_ConvergesToHydratedTranscript() {
		await using var fixture = CreateFixture(static () => "slot-1", static (_, _) => { }, 0);
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

			await host.DrainPaneAsync(CancellationToken.None);
			bridge.Clear();
			using var barrier = new Barrier(2);
			var hydrate = Task.Run(() => {
				barrier.SignalAndWait();
				session.Emit(new AgentPaneMessage { Type = "transcript-reset", ProviderId = "codex" });
				foreach (var message in hydrated) {
					session.Emit(message);
				}
			});
			var replay = Task.Run(() => {
				barrier.SignalAndWait();
				host.ReplayPane();
			});
			await Task.WhenAll(hydrate, replay);
			await host.DrainPaneAsync(CancellationToken.None);

			Assert.Equal(hydrated.Select(message => message.ItemId), VisibleItemIds(bridge));
		}
	}

	// The messages carried by the single pane snapshot the bridge received.
	private static IReadOnlyList<JsonElement> Replayed(FakeHostBridge bridge) {
		var batch = Assert.Single(bridge.PostedEventsNamed("paneSnapshot"));
		return [.. batch.GetProperty("messages").EnumerateArray()];
	}

	private static IReadOnlyList<JsonElement> Batched(FakeHostBridge bridge) {
		var batch = Assert.Single(bridge.PostedEventsNamed("paneBatch"));
		return [.. batch.GetProperty("messages").EnumerateArray()];
	}

	// The item ids the page would render, reconstructed from the posts in order: a reset clears the pane, an
	// agent-pane message (or each entry of an agent-pane-batch) appends its item (keyed, so a repeat updates in
	// place) — mirroring AgentPaneAccumulator.
	private static IReadOnlyList<string> VisibleItemIds(FakeHostBridge bridge) {
		var order = new List<string>();
		var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
		void Append(JsonElement paneMessage) {
			if (paneMessage.TryGetProperty("itemId", out var id)
				&& id.ValueKind == JsonValueKind.String
				&& id.GetString() is { } itemId
				&& !indexes.ContainsKey(itemId)) {
				indexes[itemId] = order.Count;
				order.Add(itemId);
			}
		}

		foreach (string json in bridge.Posted) {
			if (!MessageEnvelope.TryParse(json, out var envelope)
				|| envelope is not { Kind: MessageKind.Event, Feature: "agent" }) {
				continue;
			}

			switch (envelope.Name) {
				case "paneReset":
					order.Clear();
					indexes.Clear();
					break;
				case "paneSnapshot":
					order.Clear();
					indexes.Clear();
					foreach (var paneMessage in envelope.Payload.GetProperty("messages").EnumerateArray()) {
						Append(paneMessage);
					}

					break;
				case "pane":
					Append(envelope.Payload);
					break;
				case "paneBatch":
					foreach (var paneMessage in envelope.Payload.GetProperty("messages").EnumerateArray()) {
						Append(paneMessage);
					}

					break;
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

	private static HostFixture CreateFixture(Func<string> slot, Action<IFileSystem, string> seedTranscript, long paneCoalesceMs) {
		string dir = Path.Combine(Path.GetTempPath(), "weavie-agent-host-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		var fileSystem = new InMemoryFileSystem();
		string transcriptPath = Path.Combine(dir, "agent-pane.json");
		seedTranscript(fileSystem, transcriptPath);
		return CreateFixture(slot, fileSystem, transcriptPath, paneCoalesceMs);
	}

	private static HostFixture CreateFixture(
		Func<string> slot,
		IFileSystem fileSystem,
		string transcriptPath,
		long paneCoalesceMs) {
		string dir = Path.GetDirectoryName(transcriptPath)!;
		var settings = CoreSettings.CreateStore(Path.Combine(dir, "settings.toml"), enableWatcher: false);
		settings.Set(AgentSettings.PaneCoalesceMs, JsonSerializer.SerializeToElement(paneCoalesceMs));
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
			bridge.SessionFeature("agent"),
			bridge.SessionFeature("terminal.agent"),
			settings,
			new NoopPtyLauncher(),
			transcriptPath);
		return new HostFixture(bridge, session, host, registry, settings);
	}

	private sealed class BlockingReadFileSystem : IFileSystem {
		private readonly InMemoryFileSystem _inner = new();
		private readonly string _blockedPath;
		private readonly ManualResetEventSlim _release;

		public BlockingReadFileSystem(string blockedPath, ManualResetEventSlim release) {
			_blockedPath = blockedPath;
			_release = release;
			_inner.WriteAllText(
				blockedPath,
				"{\"type\":\"item-completed\",\"providerId\":\"codex\",\"text\":\"persisted after activation\"}\n");
		}

		public bool FileExists(string path) => _inner.FileExists(path);

		public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

		public bool TryGetStat(string path, out FileStat stat) => _inner.TryGetStat(path, out stat);

		public IReadOnlyList<DirectoryEntry> EnumerateDirectory(string path) => _inner.EnumerateDirectory(path);

		public string ReadAllText(string path) {
			if (string.Equals(path, _blockedPath, StringComparison.Ordinal)) {
				_release.Wait();
			}

			return _inner.ReadAllText(path);
		}

		public bool TryReadAllText(string path, out string contents) => _inner.TryReadAllText(path, out contents);

		public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);

		public void WriteAllText(string path, string contents) => _inner.WriteAllText(path, contents);

		public void WriteAllBytes(string path, byte[] contents) => _inner.WriteAllBytes(path, contents);

		public void AppendAllText(string path, string contents) => _inner.AppendAllText(path, contents);

		public void WriteAllTextAtomic(string path, string contents) => _inner.WriteAllTextAtomic(path, contents);

		public void DeleteFile(string path) => _inner.DeleteFile(path);
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
