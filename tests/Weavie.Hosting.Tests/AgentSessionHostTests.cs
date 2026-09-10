using System.Text;
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
	public async Task Pane_wire_keeps_review_paths_and_output_without_transmitting_diff_contents() {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		fixture.Session.Emit(Completed("tool", "tool output") with {
			ItemType = "tool",
			Diffs = [new AgentPaneDiff { Path = "/file", OldText = "before", NewText = new('x', 14 * 1024 * 1024) }],
			Locations = [new AgentPaneLocation { Path = "/file", Line = 10 }, new AgentPaneLocation { Path = "/file", Line = 42 }],
			Content = [new AgentPaneContent { Type = "text", Text = "rich output" }],
		});
		await fixture.Host.DrainPaneAsync(CancellationToken.None);
		var live = Assert.Single(fixture.Bridge.PostedEventsNamed("pane"));
		var batches = await HistoryBatches(fixture.Host.ReadHistory(new(null, null)));
		Assert.True(batches.Sum(batch => Encoding.UTF8.GetByteCount(batch.GetRawText())) < 4096);
		var history = Assert.Single(HistoryRecords(batches));
		Assert.Equal(live.GetRawText(), history.GetRawText());
		var diff = Assert.Single(history.GetProperty("diffs").EnumerateArray());
		Assert.Equal("path", Assert.Single(diff.EnumerateObject()).Name);
		Assert.Equal("/file", diff.GetProperty("path").GetString());
		Assert.Equal("tool output", history.GetProperty("text").GetString());
		Assert.Equal("rich output", Assert.Single(history.GetProperty("content").EnumerateArray()).GetProperty("text").GetString());
		Assert.Equal(new long[] { 10, 42 }, history.GetProperty("locations").EnumerateArray().Select(location => location.GetProperty("line").GetInt64()));
	}

	[Fact]
	public async Task StructuredUsage_IsPublishedAndReplayedForItsOwningSession() {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		var (bridge, session, host) = (fixture.Bridge, fixture.Session, fixture.Host);
		session.EmitUsage(new AgentUsageSnapshot(
			new(25000, 100000),
			[new("seven_day", AgentUsageLimitStatus.Warning, 62, DateTimeOffset.FromUnixTimeSeconds(1731547200))]));
		var published = Assert.Single(bridge.PostedEventsNamed("usage")).GetProperty("state");
		Assert.Equal(25000, published.GetProperty("contextWindow").GetProperty("usedTokens").GetInt64());
		var limit = Assert.Single(published.GetProperty("limits").EnumerateArray());
		Assert.Equal("seven_day", limit.GetProperty("id").GetString());
		Assert.Equal("warning", limit.GetProperty("status").GetString());
		Assert.Equal(62, limit.GetProperty("usedPercent").GetDouble());

		bridge.Clear();
		host.ReplayState();
		var replayed = Assert.Single(bridge.PostedEventsNamed("usage")).GetProperty("state");
		Assert.Equal(100000, replayed.GetProperty("contextWindow").GetProperty("capacityTokens").GetInt64());
		Assert.Equal(
			1731547200000,
			Assert.Single(replayed.GetProperty("limits").EnumerateArray())
				.GetProperty("resetsAtMs").GetInt64());
	}

	[Fact]
	public async Task StructuredProvider_DoesNotStartUntilSlotIsKnown() {
		string slot = string.Empty;
		// Window 0 so each live message posts its own agent-pane frame, asserted synchronously below.
		await using var fixture = CreateFixture(() => slot, 0);
		var (bridge, session, host) = (fixture.Bridge, fixture.Session, fixture.Host);

		Assert.False(session.Started);
		slot = "slot-1";
		host.Structured!.Start();
		await host.DrainPaneAsync(CancellationToken.None);

		var message = Assert.Single(bridge.PostedEventsNamed("pane"));
		Assert.Equal("started", message.GetProperty("type").GetString());

		session.Emit(new AgentPaneMessage {
			Type = "agent-message-delta",
			ProviderId = "structured",
			TurnId = "turn-1",
			ItemId = "item-1",
			Text = "hello ",
		});
		session.Emit(new AgentPaneMessage {
			Type = "agent-message-delta",
			ProviderId = "structured",
			TurnId = "turn-1",
			ItemId = "item-1",
			Text = "world",
		});
		await host.DrainPaneAsync(CancellationToken.None);
		bridge.Clear();
		var replayed = Assert.Single(await History(host), value =>
			value.GetProperty("itemId").GetString() == "item-1");
		Assert.Equal("hello world", replayed.GetProperty("text").GetString());

		session.Emit(new AgentPaneMessage {
			Type = "agent-message-delta",
			ProviderId = "structured",
			ThreadId = "thread-a",
			TurnId = "turn-shared",
			ItemId = "item-shared",
			Text = "alpha",
		});
		session.Emit(new AgentPaneMessage {
			Type = "agent-message-delta",
			ProviderId = "structured",
			ThreadId = "thread-b",
			TurnId = "turn-shared",
			ItemId = "item-shared",
			Text = "beta",
		});
		await host.DrainPaneAsync(CancellationToken.None);
		bridge.Clear();
		string?[] shared = [.. (await History(host))
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
				ProviderId = "structured",
				ThreadId = collision.Thread,
				TurnId = collision.Turn,
				ItemId = "item-collision",
				Text = collision.Text,
			});
		}
		await host.DrainPaneAsync(CancellationToken.None);
		bridge.Clear();
		string?[] collisionTexts = [.. (await History(host))
			.Where(value => value.GetProperty("itemId").GetString() == "item-collision")
			.Select(value => value.GetProperty("text").GetString())];
		Assert.Equal(collisions.Select(collision => collision.Text), collisionTexts);
	}

	[Fact]
	public async Task ReplayStateRestoresAnActiveAuthenticationTerminal() {
		await using var fixture = CreateFixture(
			static () => "slot-1",
			0,
			withAuthenticationTerminal: true);
		var terminal = Assert.IsType<AgentAuthenticationTerminal>(fixture.Host.AuthenticationTerminal);
		using var cancellation = new CancellationTokenSource();
		var authentication = terminal.RunAsync(new AgentLaunch {
			Command = "login",
			Arguments = [],
			WorkingDirectory = fixture.Workspace,
			RemoveEnvironment = [],
			Environment = new Dictionary<string, string>(StringComparer.Ordinal),
			ExecutableMode = AgentExecutableMode.SearchPath,
			WorkingDirectoryMode = AgentWorkingDirectoryMode.Fixed,
			OutputCapture = new AgentOutputCapture.Disabled(),
		}, cancellation.Token);
		fixture.Bridge.Clear();

		fixture.Host.ReplayState();

		Assert.True(Assert.Single(fixture.Bridge.PostedEventsNamed("authenticationTerminal"))
			.GetProperty("active").GetBoolean());
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authentication);
	}

	[Fact]
	public async Task History_streams_recent_records_first_without_dropping_messages() {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		for (int index = 0; index < 1000; index++) {
			fixture.Session.Emit(Completed($"item-{index}", $"line {index}"));
		}
		await fixture.Host.DrainPaneAsync(CancellationToken.None);
		var batches = await HistoryBatches(fixture.Host.ReadHistory(new(null, null)));

		Assert.True(batches.Count > 1);
		Assert.Equal("item-999", batches[0].GetProperty("messages").EnumerateArray().Last().GetProperty("itemId").GetString());
		Assert.Equal(1000, HistoryRecords(batches).Count);
		Assert.All(batches.Take(batches.Count - 1), batch => Assert.False(batch.GetProperty("complete").GetBoolean()));
		Assert.True(batches[^1].GetProperty("complete").GetBoolean());
		Assert.Empty(batches[^1].GetProperty("messages").EnumerateArray());
		Assert.All(batches, batch => Assert.Equal(1000, batch.GetProperty("count").GetInt32()));
	}

	[Fact]
	public async Task Completed_history_baseline_returns_only_later_record_revisions() {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		fixture.Session.Emit(Completed("initial", "initial"));
		await fixture.Host.DrainPaneAsync(CancellationToken.None);
		var baseline = fixture.Host.ReadHistory(new(null, null));
		Assert.Empty(fixture.Host.ReadHistory(new(baseline.Generation, baseline.Revision)).Messages);

		fixture.Session.Emit(Completed("later", "later result"));
		await fixture.Host.DrainPaneAsync(CancellationToken.None);
		var delta = fixture.Host.ReadHistory(new(baseline.Generation, baseline.Revision));
		Assert.Equal("later", Assert.Single(delta.Messages).Message.ItemId);
		Assert.True(delta.Revision > baseline.Revision);
	}

	[Fact]
	public async Task History_stream_preserves_oversized_unicode_records() {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		string text = string.Concat(Enumerable.Repeat("snowman ☃ emoji 😀 quote \\\"\n", 20_000));
		fixture.Session.Emit(Completed("oversized", text));
		await fixture.Host.DrainPaneAsync(CancellationToken.None);

		var record = Assert.Single(await History(fixture.Host));
		Assert.Equal("oversized", record.GetProperty("itemId").GetString());
		Assert.Equal(text, record.GetProperty("text").GetString());
	}

	[Fact]
	public async Task History_stream_preserves_oversized_metadata() {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		var (session, host) = (fixture.Session, fixture.Host);
		string description = string.Concat(Enumerable.Repeat("metadata ☃ 😀 \\\"\n", 30_000));
		var message = Completed("oversized-metadata", "short") with {
			Questions = [new AgentInputQuestion {
				Id = "choice",
				Header = "Choose",
				Question = "Which option?",
				AllowsOther = false,
				Kind = "string",
				Required = true,
				Format = null,
				InitialValues = ["value"],
				Minimum = null,
				Maximum = null,
				MinimumLength = null,
				MaximumLength = null,
				Pattern = null,
				Options = [new AgentInputOption { Value = "value", Label = "Value", Description = description }],
			}],
		};

		session.Emit(message);
		await host.DrainPaneAsync(CancellationToken.None);
		var record = Assert.Single(await History(host));
		Assert.Equal(description, record
			.GetProperty("questions")[0]
			.GetProperty("options")[0]
			.GetProperty("description")
			.GetString());
	}

	// The card reopens a resolved request from replayed history, so the answers have to survive the projection.
	[Fact]
	public async Task Replayed_history_keeps_the_answers_of_a_resolved_input_request() {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		var (session, host) = (fixture.Session, fixture.Host);
		session.Emit(Completed("request:1", "answered") with {
			Type = "input-resolved",
			Status = "accepted",
			Answers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) {
				["choice"] = ["two"],
			},
		});
		await host.DrainPaneAsync(CancellationToken.None);

		var record = Assert.Single(await History(host));
		Assert.Equal("two", record.GetProperty("answers").GetProperty("choice")[0].GetString());
	}

	[Fact]
	public async Task History_stream_keeps_one_immutable_revision_while_live_output_changes() {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		var delta = new AgentPaneMessage {
			Type = "agent-message-delta",
			ProviderId = "structured",
			TurnId = "turn",
			ItemId = "streaming",
			Text = new('a', 400_000),
		};
		fixture.Session.Emit(delta);
		await fixture.Host.DrainPaneAsync(CancellationToken.None);
		var snapshot = fixture.Host.ReadHistory(new(null, null));
		fixture.Session.Emit(delta with { Text = "tail" });
		await fixture.Host.DrainPaneAsync(CancellationToken.None);

		var record = Assert.Single(HistoryRecords(await HistoryBatches(snapshot)));
		Assert.Equal(delta.Text, record.GetProperty("text").GetString());
		var latest = Assert.Single(await History(fixture.Host));
		Assert.Equal(delta.Text + "tail", latest.GetProperty("text").GetString());
	}

	[Fact]
	public async Task LiveMessages_within_the_window_coalesce_into_one_batch_frame() {
		await using var fixture = CreateFixture(static () => "slot-1", 200);
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

	// The regression that stranded live pages: a provider replay used to reset the pane, and every client holding
	// the old ordinals was told to throw them away mid-load. Filling an empty pane invalidates nothing.
	[Fact]
	public async Task ProviderReplayIntoAnEmptyPane_KeepsOneGeneration() {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		var (bridge, session, host) = (fixture.Bridge, fixture.Session, fixture.Host);

		session.Replace([Completed("restored-0", "restored a"), Completed("restored-1", "restored b")]);
		await host.DrainPaneAsync(CancellationToken.None);

		Assert.Empty(bridge.PostedEventsNamed("paneReset"));
		var live = bridge.PostedEventsNamed("pane");
		Assert.Equal(2, live.Count);
		Assert.Single(live.Select(message => message.GetProperty("generation").GetInt64()).Distinct());
		Assert.Equal(
			["restored-0", "restored-1"],
			(await History(host)).Select(message => message.GetProperty("itemId").GetString()));
	}

	// A replay over existing content genuinely voids those ordinals, so that case must still announce a reset.
	[Fact]
	public async Task ProviderReplayOverExistingContent_ResetsTheGeneration() {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		var (bridge, session, host) = (fixture.Bridge, fixture.Session, fixture.Host);
		session.Emit(Completed("stale-0", "stale"));
		await host.DrainPaneAsync(CancellationToken.None);
		long before = bridge.PostedEventsNamed("pane").Max(message => message.GetProperty("generation").GetInt64());
		bridge.Clear();

		session.Replace([Completed("restored-0", "restored a")]);
		await host.DrainPaneAsync(CancellationToken.None);

		Assert.NotEmpty(bridge.PostedEventsNamed("paneReset"));
		var restored = Assert.Single(await History(host));
		Assert.Equal("restored-0", restored.GetProperty("itemId").GetString());
		Assert.True(restored.GetProperty("generation").GetInt64() > before);
	}

	[Fact]
	public async Task FullSnapshotReplacesPrimaryAndSideTranscripts() {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		var (session, host) = (fixture.Session, fixture.Host);
		session.Emit(Completed("old-primary", "old primary"));
		session.Emit(SideCompleted("old-side", "old side"));
		session.Replace([Completed("new-primary", "new primary"), SideCompleted("new-side", "restored side")]);
		await host.DrainPaneAsync(CancellationToken.None);

		var history = await History(host);
		Assert.DoesNotContain(history, message => message.GetProperty("itemId").GetString() == "old-primary");
		Assert.Contains(history, message => message.GetProperty("itemId").GetString() == "new-primary");
		Assert.DoesNotContain(history, message => message.GetProperty("itemId").GetString() == "old-side");
		Assert.Contains(history, message => message.GetProperty("itemId").GetString() == "new-side");
	}

	[Fact]
	public async Task CompletedPlan_IsAvailableOnlyForItsExactCurrentIdentity() {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		var (session, host) = (fixture.Session, fixture.Host);
		const string threadId = "thread-plan";
		const string turnId = "turn-plan";
		const string itemId = "item-plan";

		session.Emit(new AgentPaneMessage {
			Type = "plan-delta",
			ProviderId = "structured",
			ThreadId = threadId,
			TurnId = turnId,
			ItemId = itemId,
			ItemType = "plan",
			Text = "# Draft",
		});
		Assert.False(host.TryGetCompletedPlan(threadId, turnId, itemId, out _));

		session.Emit(new AgentPaneMessage {
			Type = "item-completed",
			ProviderId = "structured",
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

		session.Emit(new AgentPaneMessage { Type = "transcript-reset", ProviderId = "structured" });
		Assert.False(host.TryGetCompletedPlan(threadId, turnId, itemId, out _));
	}

	[Fact]
	public async Task LaterTerminalOutcome_ReconcilesTheCompletedItemInPlace() {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		var (session, host) = (fixture.Session, fixture.Host);
		session.Emit(Completed("task", "provisional"));
		session.Emit(Completed("task", "authoritative") with { Status = "failed" });
		await host.DrainPaneAsync(CancellationToken.None);

		var item = Assert.Single(await History(host), message =>
			message.GetProperty("itemId").GetString() == "task");

		Assert.Equal("authoritative", item.GetProperty("text").GetString());
		Assert.Equal("failed", item.GetProperty("status").GetString());
	}

	// A page may request history while async provider resume replaces it. The snapshot may see either generation,
	// but the next read must converge to the authoritative replacement.
	[Fact]
	public async Task HistoryRead_RacingHydrate_ConvergesToHydratedTranscript() {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		var (session, host) = (fixture.Session, fixture.Host);

		// A resumed thread re-emits transcript-reset + its completed items; this is the authoritative end state.
		AgentPaneMessage[] hydrated = [Completed("fresh-0", "fresh a"), Completed("fresh-1", "fresh b")];

		for (int iteration = 0; iteration < 40; iteration++) {
			// Restore a wide prior generation so the history copy and replacement overlap real work.
			session.Emit(new AgentPaneMessage { Type = "transcript-reset", ProviderId = "structured" });
			for (int i = 0; i < 100; i++) {
				session.Emit(Completed($"seed-{i}", $"seed {i}"));
			}

			await host.DrainPaneAsync(CancellationToken.None);
			using var barrier = new Barrier(2);
			var hydrate = Task.Run(() => {
				barrier.SignalAndWait();
				session.Replace(hydrated);
			});
			var read = Task.Run(() => {
				barrier.SignalAndWait();
				host.ReadHistory(new(null, null));
			});
			await Task.WhenAll(hydrate, read);
			await host.DrainPaneAsync(CancellationToken.None);

			Assert.Equal(hydrated.Select(message => message.ItemId),
				(await History(host)).Select(message => message.GetProperty("itemId").GetString()));
		}
	}

	private static async Task<IReadOnlyList<JsonElement>> History(AgentSessionHost host) =>
		HistoryRecords(await HistoryBatches(host.ReadHistory(new(null, null))));

	private static IReadOnlyList<JsonElement> HistoryRecords(IReadOnlyList<JsonElement> batches) =>
		[.. batches.SelectMany(batch => batch.GetProperty("messages").EnumerateArray())
			.OrderBy(message => message.GetProperty("ordinal").GetInt64())];

	private static async Task<IReadOnlyList<JsonElement>> HistoryBatches(AgentPaneHistory snapshot) {
		using var output = new MemoryStream();
		await AgentPaneProtocol.WriteHistoryAsync(snapshot, output, CancellationToken.None);
		return [.. Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
			.Select(line => JsonSerializer.Deserialize<JsonElement>(line))];
	}

	private static IReadOnlyList<JsonElement> Batched(FakeHostBridge bridge) {
		var batch = Assert.Single(bridge.PostedEventsNamed("paneBatch"));
		return [.. batch.GetProperty("messages").EnumerateArray()];
	}

	private static AgentPaneMessage Completed(string itemId, string text) => new() {
		Type = "item-completed",
		ProviderId = "structured",
		TurnId = "turn",
		ItemId = itemId,
		Text = text,
		Status = "completed",
	};

	private static AgentPaneMessage SideCompleted(string itemId, string text) => Completed(itemId, text) with {
		ThreadId = "side-session",
		ConversationId = "side-conversation",
		AnchorTurnId = "turn",
		IsPrimaryThread = false,
	};

	private static HostFixture CreateFixture(Func<string> slot, long paneCoalesceMs) =>
		CreateFixture(slot, paneCoalesceMs, withAuthenticationTerminal: false);

	private static HostFixture CreateFixture(
		Func<string> slot,
		long paneCoalesceMs,
		bool withAuthenticationTerminal) {
		var dir = new TempDirectory("weavie-agent-host-tests");
		var fileSystem = new InMemoryFileSystem();
		var settings = CoreSettings.CreateStore(dir.Combine("settings.toml"), enableWatcher: false);
		settings.Set(AgentSettings.PaneCoalesceMs, JsonSerializer.SerializeToElement(paneCoalesceMs));
		var commandRegistry = CoreCommands.CreateRegistry();
		var bridge = new FakeHostBridge();
		var registry = new CapabilityRegistryHost(
			AgentSessionCredential.Create(),
			FakeDiffPresenter.AlwaysKeep(),
			[dir.Path],
			"weavie",
			settings,
			new LayoutStore(fileSystem, LayoutPanes.CreateRegistry(), "/layout.json"),
			new EditorStore(),
			exposeIdeTools: true,
			new CommandDispatcher(commandRegistry),
			new KeybindingStore(commandRegistry, dir.Combine("keybindings.json"), enableWatcher: false),
			new ThemeOverridesStore(fileSystem, "/theme-overrides.json"),
			slot);
		var session = new FakeStructuredSession();
		IAgentAuthenticationTerminal authenticationTerminal = withAuthenticationTerminal
			? new AgentAuthenticationTerminal(
				bridge.SessionFeature("agent"),
				bridge.SessionFeature("terminal.agent"),
					settings,
					new NoopPtyLauncher(),
					dir.Path,
					dir.Combine("authentication.scrollback"))
			: UnavailableAgentAuthenticationTerminal.Instance;
		var host = new AgentSessionHost(
			new FakeStructuredProvider(session),
			new AgentSessionContext {
				Settings = settings,
				Workspace = dir.Path,
				FileSystem = fileSystem,
				Registry = registry,
				DiffPresenter = FakeDiffPresenter.AlwaysKeep(),
				Editor = new EditorStore(),
				Runtime = new HostRuntimeInfo(HostTransport.Local, Managed: false, "test"),
				Events = new NullAgentEventSink(),
				CurrentSessionId = slot,
				AuthenticationTerminal = authenticationTerminal,
			},
			bridge.SessionFeature("agent"),
			bridge.SessionFeature("terminal.agent"),
			settings,
			new NoopPtyLauncher());
		return new HostFixture(bridge, session, host, registry, settings, dir);
	}

	private sealed class HostFixture(
		FakeHostBridge bridge,
		FakeStructuredSession session,
		AgentSessionHost host,
		CapabilityRegistryHost registry,
		SettingsStore settings,
		TempDirectory workspace) : IAsyncDisposable {
		public FakeHostBridge Bridge => bridge;

		public FakeStructuredSession Session => session;

		public AgentSessionHost Host => host;

		public string Workspace => workspace.Path;

		public async ValueTask DisposeAsync() {
			await host.DisposeAsync();
			await registry.DisposeAsync();
			settings.Dispose();
			workspace.Dispose();
		}
	}

	private sealed class FakeStructuredProvider(FakeStructuredSession session) : IAgentProvider {
		public AgentProviderInfo Info { get; } = new() {
			Id = "structured",
			Name = "Structured",
			Capabilities = AgentProviderCapabilities.StructuredPane,
			Available = true,
		};

		public IAgentSession CreateSession(AgentSessionContext context) => session;
	}

	private sealed class FakeStructuredSession : IStructuredAgentSession, IStructuredAgentUsage {
		public event Action<AgentPaneMessage>? PaneMessage;
		public event Action<IReadOnlyList<AgentPaneMessage>>? PaneSnapshot;
		public event Action<AgentUsageSnapshot>? UsageChanged;

		public event Action<IReadOnlyList<AgentTurnSubmission>>? QueuedSubmissionsChanged { add { } remove { } }

		public IReadOnlyList<AgentTurnSubmission> QueuedSubmissions => [];

		public AgentUsageSnapshot Snapshot { get; private set; } = new(null, []);

		public bool Started { get; private set; }

		public void Start() {
			Started = true;
			PaneMessage?.Invoke(new AgentPaneMessage { Type = "started", ProviderId = "structured" });
		}

		public void Emit(AgentPaneMessage message) => PaneMessage?.Invoke(message);

		public void Replace(IReadOnlyList<AgentPaneMessage> messages) => PaneSnapshot?.Invoke(messages);

		public void EmitUsage(AgentUsageSnapshot usage) {
			Snapshot = usage;
			UsageChanged?.Invoke(usage);
		}

		public void Submit(AgentTurnSubmission submission) => throw new NotSupportedException();

		public void PrefillPrompt(string prompt) => throw new NotSupportedException();

		public void Interrupt() => throw new NotSupportedException();

		public void Restart() => throw new NotSupportedException();

		public void StartNewConversation() => throw new NotSupportedException();

		public void ResolvePermission(string requestId, string optionId) => throw new NotSupportedException();

		public void ResolveInput(
			string requestId,
			string action,
			IReadOnlyDictionary<string, IReadOnlyList<string>> answers) =>
			throw new NotSupportedException();

		public void Authenticate(
			string requestId,
			string methodId,
			IReadOnlyDictionary<string, IReadOnlyList<string>> answers) =>
			throw new NotSupportedException();

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class NullAgentEventSink : IAgentEventSink {
		public AgentEventFeedback Observe(AgentEvent value) => AgentEventFeedback.None;
	}
}
