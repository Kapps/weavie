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
	public async Task History_is_byte_paged_without_dropping_messages() {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		var (bridge, session, host) = (fixture.Bridge, fixture.Session, fixture.Host);

		host.Structured!.Start();
		// This is far past the remote bridge's logical-message outbox; history must remain pull-paged.
		for (int i = 0; i < 1000; i++) {
			session.Emit(Completed($"item-{i}", $"line {i}"));
		}
		await host.DrainPaneAsync(CancellationToken.None);
		var pages = await HistoryPages(host);

		Assert.True(pages.Count > 1);
		Assert.Equal(1001, AssembleHistory(pages).Count);
	}

	[Fact]
	public async Task Completed_history_baseline_returns_only_later_record_revisions() {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		var (session, host) = (fixture.Session, fixture.Host);
		host.Structured!.Start();
		for (int index = 0; index < 10; index++) {
			session.Emit(Completed($"initial-{index}", $"initial {index}"));
		}
		await host.DrainPaneAsync(CancellationToken.None);
		var initial = await HistoryPages(host);
		var baseline = initial[0];

		var unchanged = await host.ReadHistoryPageFromBaselineAsync(
			new AgentPaneHistoryRequest(null, baseline.Generation, baseline.Revision),
			CancellationToken.None);
		Assert.Empty(unchanged.Messages);
		Assert.Null(unchanged.Cursor);

		session.Emit(Completed("later", "later result"));
		await host.DrainPaneAsync(CancellationToken.None);
		var delta = await host.ReadHistoryPageFromBaselineAsync(
			new AgentPaneHistoryRequest(null, baseline.Generation, baseline.Revision),
			CancellationToken.None);
		var message = Assert.Single(AssembleHistory([delta]));
		Assert.Equal("later", message.GetProperty("itemId").GetString());
		Assert.True(delta.Revision > baseline.Revision);
	}

	[Fact]
	public async Task Oversized_history_record_is_fragmented_within_the_page_budget() {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		var (session, host) = (fixture.Session, fixture.Host);
		string text = string.Concat(Enumerable.Repeat("snowman ☃ emoji 😀 quote \\\"\n", 20_000));

		session.Emit(Completed("oversized", text));
		await host.DrainPaneAsync(CancellationToken.None);
		var pages = await HistoryPages(host);
		AgentPaneFragment[] fragments = [.. pages.SelectMany(page => page.Messages)];

		Assert.True(fragments.Length > 1);
		Assert.All(pages, page => Assert.True(
			JsonSerializer.SerializeToUtf8Bytes(AgentPaneProtocol.HistoryPage(page)).Length
			<= AgentSessionHost.HistoryPageTargetBytes));
		string json = string.Concat(fragments.Select(fragment => fragment.Json));
		var record = JsonDocument.Parse(json).RootElement;
		Assert.Equal("oversized", record.GetProperty("itemId").GetString());
		Assert.Equal(text, record.GetProperty("text").GetString());
		Assert.Equal(
			fragments.Select(fragment => fragment.JsonOffset),
			fragments.Select((_, index) => fragments.Take(index).Sum(
				fragment => fragment.Json.Length)));
		Assert.All(fragments, fragment => Assert.Equal(json.Length, fragment.JsonLength));
	}

	[Fact]
	public void History_fragment_measure_matches_the_serialized_wire_size() {
		var record = new AgentPaneRecord(
			12,
			-345,
			long.MinValue,
			Completed("measure", "quote \" slash \\ snowman ☃ emoji 😀"));
		string json = AgentPaneProtocol.Serialize(record);
		var fragment = new AgentPaneFragment(record, json[7..^3], 7, json.Length);
		byte[] expected = JsonSerializer.SerializeToUtf8Bytes(new {
			generation = record.Generation,
			ordinal = record.Ordinal,
			revision = record.Revision,
			jsonOffset = fragment.JsonOffset,
			jsonLength = fragment.JsonLength,
			json = fragment.Json,
		});

		Assert.Equal(expected.Length, AgentPaneProtocol.Measure(fragment));
	}

	[Fact]
	public async Task Oversized_history_metadata_is_fragmented_within_the_page_budget() {
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
		var pages = await HistoryPages(host);
		var fragments = pages.SelectMany(page => page.Messages).ToArray();

		Assert.True(fragments.Length > 1);
		Assert.All(pages, page => Assert.True(
			JsonSerializer.SerializeToUtf8Bytes(AgentPaneProtocol.HistoryPage(page)).Length
			<= AgentSessionHost.HistoryPageTargetBytes));
		var record = Assert.Single(AssembleHistory(pages));
		Assert.Equal(description, record
			.GetProperty("questions")[0]
			.GetProperty("options")[0]
			.GetProperty("description")
			.GetString());
	}

	[Fact]
	public async Task Fragmented_history_read_keeps_one_immutable_revision_while_live_output_changes() {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		var (session, host) = (fixture.Session, fixture.Host);
		string initial = new('a', AgentSessionHost.HistoryPageTargetBytes * 2);
		var delta = new AgentPaneMessage {
			Type = "agent-message-delta",
			ProviderId = "structured",
			TurnId = "turn",
			ItemId = "streaming",
			Text = initial,
		};

		session.Emit(delta);
		await host.DrainPaneAsync(CancellationToken.None);
		var first = await host.ReadHistoryPageAsync(null, CancellationToken.None);
		Assert.NotNull(first.Cursor?.JsonBefore);

		session.Emit(delta with { Text = "tail" });
		await host.DrainPaneAsync(CancellationToken.None);
		var pages = new List<AgentPaneHistoryPage> { first };
		var cursor = first.Cursor;
		while (cursor is not null) {
			var page = await host.ReadHistoryPageAsync(cursor, CancellationToken.None);
			pages.Insert(0, page);
			cursor = page.Cursor;
		}
		var fragments = pages.SelectMany(page => page.Messages).ToArray();
		var record = Assert.Single(AssembleHistory(pages));
		Assert.Equal(initial, record.GetProperty("text").GetString());
		Assert.Single(fragments.Select(fragment => fragment.Record.Revision).Distinct());

		var latest = Assert.Single(await History(host));
		Assert.Equal(initial + "tail", latest.GetProperty("text").GetString());
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

	// A page may request history while async provider resume replaces it. The cursor may see either generation,
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
			var read = Task.Run(async () => {
				barrier.SignalAndWait();
				await host.ReadHistoryPageAsync(null, CancellationToken.None);
			});
			await Task.WhenAll(hydrate, read);
			await host.DrainPaneAsync(CancellationToken.None);

			Assert.Equal(hydrated.Select(message => message.ItemId),
				(await History(host)).Select(message => message.GetProperty("itemId").GetString()));
		}
	}

	private static async Task<IReadOnlyList<JsonElement>> History(AgentSessionHost host) =>
		AssembleHistory(await HistoryPages(host));

	private static IReadOnlyList<JsonElement> AssembleHistory(IReadOnlyList<AgentPaneHistoryPage> pages) =>
		[.. pages
			.SelectMany(page => page.Messages)
			.GroupBy(fragment => (
				fragment.Record.Generation,
				fragment.Record.Ordinal,
				fragment.Record.Revision))
			.Select(fragments => JsonDocument.Parse(
				string.Concat(fragments.Select(fragment => fragment.Json))).RootElement.Clone())];

	private static async Task<IReadOnlyList<AgentPaneHistoryPage>> HistoryPages(AgentSessionHost host) {
		var pages = new List<AgentPaneHistoryPage>();
		AgentPaneHistoryCursor? cursor = null;
		do {
			var page = await host.ReadHistoryPageAsync(cursor, CancellationToken.None);
			pages.Insert(0, page);
			cursor = page.Cursor;
		} while (cursor is not null);
		return pages;
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

	private static HostFixture CreateFixture(Func<string> slot, long paneCoalesceMs) =>
		CreateFixture(slot, paneCoalesceMs, withAuthenticationTerminal: false);

	private static HostFixture CreateFixture(
		Func<string> slot,
		long paneCoalesceMs,
		bool withAuthenticationTerminal) {
		string dir = Path.Combine(Path.GetTempPath(), "weavie-agent-host-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		var fileSystem = new InMemoryFileSystem();
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
		IAgentAuthenticationTerminal authenticationTerminal = withAuthenticationTerminal
			? new AgentAuthenticationTerminal(
				bridge.SessionFeature("agent"),
				bridge.SessionFeature("terminal.agent"),
					settings,
					new NoopPtyLauncher(),
					dir,
					Path.Combine(dir, "authentication.scrollback"))
			: UnavailableAgentAuthenticationTerminal.Instance;
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
		string workspace) : IAsyncDisposable {
		public FakeHostBridge Bridge => bridge;

		public FakeStructuredSession Session => session;

		public AgentSessionHost Host => host;

		public string Workspace => workspace;

		public async ValueTask DisposeAsync() {
			await host.DisposeAsync();
			await registry.DisposeAsync();
			settings.Dispose();
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

		public void Authenticate(string methodId, IReadOnlyDictionary<string, IReadOnlyList<string>> answers) =>
			throw new NotSupportedException();

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class NullAgentEventSink : IAgentEventSink {
		public AgentEventFeedback Observe(AgentEvent value) => AgentEventFeedback.None;
	}
}
