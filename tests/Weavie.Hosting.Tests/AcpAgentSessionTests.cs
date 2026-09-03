using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Editor;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpAgentSessionTests {
	[Fact]
	public async Task NativeSession_StopCancelsAuthenticationAndIgnoresLateSuccess() {
		await using var fixture = AcpAgentSessionFixture.CreateHeldAuthenticationAdapter();
		fixture.Session.Start();
		var authentication = await fixture.WaitForMessageAsync(message => message.Type == "authentication-requested");

		fixture.Session.Authenticate(
			Assert.IsType<string>(authentication.RequestId),
			"fake-login",
			new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
		await Wait.UntilAsync(() => File.Exists(Path.Combine(fixture.Workspace, "authentication-started")));
		fixture.Session.Interrupt();
		var cancelled = await fixture.WaitForMessageAsync(message => message.Type == "authentication-resolved");
		await Wait.UntilAsync(() => File.Exists(Path.Combine(fixture.Workspace, "authentication-completed")));
		await Task.Delay(250);

		Assert.Equal("cancelled", cancelled.Status);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "authentication-resolved"
			&& message.Status == "accepted");
		Assert.DoesNotContain(fixture.Events.Values, value => value is AgentSessionStarted);
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_PersistsOnlyAfterTheFirstSubmittedTurn() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		Assert.Null(fixture.Sessions.Resolve("fake", fixture.Workspace));
		fixture.Submit("hello");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal("fake-session", fixture.Sessions.Resolve("fake", fixture.Workspace));
	}

	[Fact]
	public async Task NativeSession_TerminatesTheAdapterBeforeAProviderCanRunWithoutPersistence() {
		await using var fixture = AcpAgentSessionFixture.CreateWithFailingPersistence(allowAllPermissions: true);
		await fixture.StartAsync();
		string mutation = Path.Combine(fixture.Workspace, "provider-mutation.txt");

		fixture.Submit("persist-probe:" + mutation);
		var failed = await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		await fixture.WaitForMessageAsync(message => message.Type == "error"
			&& message.Text?.Contains("persistence failure", StringComparison.OrdinalIgnoreCase) == true);
		await fixture.Events.WaitForAsync(value => value is AgentProcessChanged {
			Change.State: Weavie.Core.Processes.SupervisorState.Idle,
		});
		await Task.Delay(TimeSpan.FromMilliseconds(500));

		Assert.Equal("failed", failed.Status);
		Assert.False(File.Exists(mutation));
		Assert.DoesNotContain(fixture.Messages, message =>
			message.Text == "persistence failure did not stop the provider");
		Assert.Equal(SessionStatus.Error, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_IgnoresLiveProviderEchoesOfTheSubmittedPrompt() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("echo-user");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Single(fixture.Messages, message => message.Type == "user-message" && message.Text == "echo-user");
	}

	[Fact]
	public async Task NativeSession_MapsControlsCommandsAndRichUpdates() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		var controls = await fixture.StartAsync();

		var model = Assert.Single(controls.Axes, axis => axis.Id == "model");
		Assert.Equal("Alpha", model.ValueLabel);
		Assert.Equal(["Stable", "Preview"], model.Options.Select(option => option.Group));
		Assert.Equal("false", Assert.Single(controls.Axes, axis => axis.Id == "fast").Value);
		Assert.Equal("default", Assert.Single(controls.Axes, axis => axis.Id == "mode").Value);
		var clear = Assert.Single(controls.Slash, command => command.Name == "clear");
		Assert.Equal(AgentSlashEntryKind.WeavieCommand, clear.Kind);
		Assert.Equal(CoreCommands.ClearAgentConversation, clear.CommandId);
		var btw = Assert.Single(controls.Slash, command => command.Name == "btw");
		Assert.Equal(AgentSlashEntryKind.WeavieCommand, btw.Kind);
		Assert.Equal(CoreCommands.AskAgentAside, btw.CommandId);
		Assert.Equal("question", btw.InputName);
		var compact = Assert.Single(controls.Slash, command => command.Name == "compact");
		Assert.Equal(AgentSlashEntryKind.ProviderCommand, compact.Kind);
		Assert.Null(compact.InputHint);
		Assert.Equal("<focus>", Assert.Single(controls.Slash, command => command.Name == "review").InputHint);

		fixture.Submit("rich");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		var messages = fixture.Messages;
		Assert.Contains(messages, message => message.Type == "item-completed"
			&& message.ItemType == "thought" && message.Text == "inspect");
		var edit = Assert.Single(messages, message => message.Type == "item-completed" && message.ItemId == "tool:edit");
		Assert.Equal("sample.txt", Path.GetFileName(Assert.Single(edit.Locations!).Path));
		Assert.Equal("new", Assert.Single(edit.Diffs!).NewText);
		Assert.Contains(messages, message => message.ItemType == "progress"
			&& message.Text!.Contains("[~] Implement", StringComparison.Ordinal));
		Assert.DoesNotContain(messages, message => message.ItemType == "plan");
		var usage = fixture.Session.Snapshot;
		Assert.Equal(new AgentContextWindowUsage(123, 4096), usage.ContextWindow);
		var limit = Assert.Single(usage.Limits);
		Assert.Equal("seven_day", limit.Id);
		Assert.Equal(AgentUsageLimitStatus.Warning, limit.Status);
		Assert.Equal(62, limit.UsedPercent);
		Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(4102444800), limit.ResetsAt);
		Assert.Contains(messages, message => message.Type == "item-completed" && message.Text == "rich response");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_ForksAndContinuesSideConversationWithoutPromptingPrimarySession() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("primary context");
		await fixture.WaitForMessageAsync(message =>
			message.Type == "turn-completed" && message.IsPrimaryThread is not false);

		fixture.Session.AskAside("why this design?");
		var marker = await fixture.WaitForMessageAsync(message => message.Type == "side-conversation-started");
		string conversationId = Assert.IsType<string>(marker.ConversationId);
		var answer = await fixture.WaitForMessageAsync(message =>
			message.Type == "item-completed"
			&& message.ConversationId == conversationId
			&& message.Text == "echo: why this design?");

		Assert.False(answer.IsPrimaryThread);
		Assert.Equal(conversationId, answer.ConversationId);
		Assert.Equal("1", answer.AnchorTurnId);
		Assert.DoesNotContain(fixture.Messages, message =>
			message.IsPrimaryThread == true && message.Text == "why this design?");

		fixture.Session.ReplyAside(conversationId, "and the tradeoff?");
		await fixture.WaitForMessageAsync(message =>
			message.Type == "item-completed"
			&& message.ConversationId == conversationId
			&& message.TurnId == "2"
			&& message.Text == "echo: and the tradeoff?");
		await fixture.WaitForMessageAsync(message =>
			message.Type == "turn-completed"
			&& message.ConversationId == conversationId
			&& message.TurnId == "2");
		string fork = Assert.Single(File.ReadAllLines(Path.Combine(fixture.FakeAcpStateDirectory, "forks.log")));
		string sideSessionId = fork[(fork.IndexOf("->", StringComparison.Ordinal) + 2)..];
		string[] prompts = File.ReadAllLines(Path.Combine(fixture.FakeAcpStateDirectory, "prompts.log"));
		Assert.Equal(3, prompts.Length);
		Assert.StartsWith("fake-session:primary context", prompts[0], StringComparison.Ordinal);
		Assert.All(prompts.Skip(1), prompt => Assert.StartsWith(sideSessionId + ":", prompt, StringComparison.Ordinal));
		Assert.Equal(
			sideSessionId,
			Assert.Single(File.ReadAllLines(Path.Combine(fixture.FakeAcpStateDirectory, "loads.log"))));
	}

	[Fact]
	public async Task NativeSession_QueuesIndependentSideConversations() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Session.AskAside("first aside");
		fixture.Session.AskAside("second aside");
		var first = await fixture.WaitForMessageAsync(message =>
			message.Type == "item-completed" && message.Text == "echo: first aside");
		var second = await fixture.WaitForMessageAsync(message =>
			message.Type == "item-completed" && message.Text == "echo: second aside");

		Assert.NotEqual(first.ConversationId, second.ConversationId);
		Assert.Equal(2, File.ReadAllLines(Path.Combine(fixture.FakeAcpStateDirectory, "forks.log")).Length);
	}

	[Fact]
	public async Task NativeSession_SendsGuidanceWhenForkingAnEmptyPrimaryContext() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Session.AskAside("context");
		var answer = await fixture.WaitForMessageAsync(message =>
			message.Type == "item-completed" && message.Text?.StartsWith("context:", StringComparison.Ordinal) == true);

		Assert.Equal("context:guidance=True;selection=False", answer.Text);
		Assert.NotNull(answer.ConversationId);
	}

	[Fact]
	public async Task NativeSession_HidesBtwWhenForkOrLoadIsUnavailable() {
		await using var fixture = AcpAgentSessionFixture.CreateMinimalCapabilitiesAdapter();
		var controls = await fixture.StartAsync();

		Assert.DoesNotContain(controls.Slash, command => command.Name == "btw");
	}

	[Fact]
	public async Task NativeSession_RoutesSideConversationPermissionBackToItsChildRuntime() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: false, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Session.AskAside("permission");
		var request = await fixture.WaitForMessageAsync(message =>
			message.Type == "approval-requested" && message.ConversationId is not null);
		fixture.Session.ResolvePermission(Assert.IsType<string>(request.RequestId), "allow-once");
		var answer = await fixture.WaitForMessageAsync(message =>
			message.Type == "item-completed" && message.Text == "permission: allow-once");

		Assert.Equal(request.ConversationId, answer.ConversationId);
		Assert.False(answer.IsPrimaryThread);
	}

	[Fact]
	public async Task NativeSession_TracksSideConversationMutationsInTheOwningSession() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Session.AskAside("rich");
		var terminal = await fixture.WaitForMessageAsync(message =>
			message.ConversationId is not null
			&& message.Type is "turn-completed" or "side-conversation-failed");
		Assert.True(
			terminal.Type == "turn-completed",
			$"Side turn ended as {terminal.Type}: {terminal.Summary ?? terminal.Text}");

		var starting = Assert.Single(fixture.Events.Values.OfType<AgentToolStarting>());
		Assert.IsType<AgentMutation.File>(starting.Mutation);
		Assert.Single(fixture.Events.Values.OfType<AgentToolCompleted>());
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_KeepsSideRuntimeUntilBackgroundWorkSettles() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Session.AskAside("delayed-background");
		var turn = await fixture.WaitForMessageAsync(message =>
			message.Type == "turn-completed" && message.ConversationId is not null);
		Assert.Equal(SessionStatus.Waiting, fixture.Events.Status.Status);
		var answer = await fixture.WaitForMessageAsync(message =>
			message.Type == "item-completed" && message.Text == "delayed background finished");
		await Wait.UntilAsync(() => fixture.Events.Status.Status == SessionStatus.Idle);

		Assert.Equal(turn.ConversationId, answer.ConversationId);
	}

	[Fact]
	public async Task NativeSession_RoutesSideConversationAuthenticationByRequestIdentity() {
		await using var fixture = AcpAgentSessionFixture.CreateAgentAuthenticationAdapter();
		fixture.Session.Start();
		var primaryAuthentication = await fixture.WaitForMessageAsync(message =>
			message.Type == "authentication-requested" && message.ConversationId is null);
		fixture.Session.Authenticate(
			Assert.IsType<string>(primaryAuthentication.RequestId),
			"fake-login",
			new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
		await fixture.WaitForControlsAsync(state => state.Axes.Count > 0);

		fixture.Session.AskAside("authenticated aside");
		var sideAuthentication = await fixture.WaitForMessageAsync(message =>
			message.Type == "authentication-requested" && message.ConversationId is not null);
		fixture.Session.Authenticate(
			Assert.IsType<string>(sideAuthentication.RequestId),
			"fake-login",
			new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
		var answer = await fixture.WaitForMessageAsync(message =>
			message.Type == "item-completed" && message.Text == "echo: authenticated aside");

		Assert.Equal(sideAuthentication.ConversationId, answer.ConversationId);
	}

	[Fact]
	public async Task NativeSession_InterruptsSideLoadAuthenticationAndDispatchesPrimaryPrompt() {
		await using var fixture = AcpAgentSessionFixture.CreateSideHeldAuthenticationAdapter();
		await fixture.StartAsync();

		fixture.Session.AskAside("authentication");
		var authentication = await fixture.WaitForMessageAsync(message =>
			message.Type == "authentication-requested" && message.ConversationId is not null);
		fixture.Submit("after interruption");
		fixture.Session.Interrupt();

		var terminal = await fixture.WaitForMessageAsync(message =>
			message.Type == "side-conversation-failed"
			&& message.ConversationId == authentication.ConversationId);
		var answer = await fixture.WaitForMessageAsync(message =>
			message.Type == "item-completed" && message.Text == "echo: after interruption");

		Assert.Equal(authentication.ConversationId, terminal.ConversationId);
		Assert.NotEqual(false, answer.IsPrimaryThread);
	}

	[Fact]
	public async Task NativeSession_InterruptsActiveSideBeforeDispatchingTheNextSide() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Session.AskAside("hold");
		fixture.Session.AskAside("next aside");
		var held = await fixture.WaitForMessageAsync(message =>
			message.Type == "item-started" && message.ItemId == "tool:hold" && message.ConversationId is not null);
		fixture.Session.Interrupt();
		var interrupted = await fixture.WaitForMessageAsync(message =>
			message.Type == "turn-completed"
			&& message.ConversationId == held.ConversationId
			&& message.Status == "cancelled");
		var next = await fixture.WaitForMessageAsync(message =>
			message.Type == "item-completed" && message.Text == "echo: next aside");

		Assert.Equal(held.ConversationId, interrupted.ConversationId);
		Assert.NotEqual(held.ConversationId, next.ConversationId);
	}

	[Fact]
	public async Task NativeSession_TerminalizesASideRuntimeThatCrashesWhileIdle() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Session.AskAside("crash-when-released");
		var completed = await fixture.WaitForMessageAsync(message =>
			message.Type == "turn-completed" && message.ConversationId is not null);
		File.WriteAllText(Path.Combine(fixture.Workspace, "release-crash"), string.Empty);
		var terminal = await fixture.WaitForMessageAsync(message =>
			message.Type == "side-conversation-failed"
			&& message.ConversationId == completed.ConversationId);

		Assert.Equal(completed.ConversationId, terminal.ConversationId);
		var error = Assert.Throws<InvalidOperationException>(() =>
			fixture.Session.ReplyAside(Assert.IsType<string>(completed.ConversationId), "still there?"));
		Assert.Contains("no longer available", error.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task NativeSession_NewConversationDetachesActiveSideRuntime() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Session.AskAside("hold");
		await fixture.WaitForMessageAsync(message =>
			message.Type == "item-started" && message.ItemId == "tool:hold" && message.ConversationId is not null);

		fixture.Session.StartNewConversation();
		int reset = fixture.Messages.ToList().FindLastIndex(message => message.Type == "transcript-reset");
		await fixture.WaitForControlsAsync(state => state.Axes.Count > 0);
		await Task.Delay(250);

		Assert.DoesNotContain(fixture.Messages.Skip(reset + 1), message => message.ConversationId is not null);
	}

	[Fact]
	public async Task NativeSession_InvokesProviderCommandsAsOneIsolatedPromptBlock() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.SubmitCommand("compact", "/COMPACT");
		await fixture.WaitForMessageAsync(message => message.Text == "Compacting completed.");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Contains(fixture.Messages, message => message.Type == "user-command" && message.Text == "/compact");
		Assert.True(File.Exists(Path.Combine(fixture.Workspace, "compact-executed")));
		fixture.Submit("context");
		await fixture.WaitForMessageAsync(message => message.Text == "context:guidance=True;selection=False");
	}

	[Fact]
	public async Task NativeSession_PreservesProviderCommandArguments() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.SubmitCommand("review", "/review focus on tests");
		await fixture.WaitForMessageAsync(message => message.Text == "review command: focus on tests");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
	}

	[Fact]
	public async Task NativeSession_TreatsProviderInputHintsAsOptional() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.SubmitCommand("review", "/review");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Contains(fixture.Messages, message => message.Type == "user-command" && message.Text == "/review");
	}

	[Fact]
	public async Task NativeSession_TracksProviderCommandsAsActiveTurns() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.SubmitCommand("hold-command", "/hold-command");
		await Wait.UntilAsync(() => File.Exists(Path.Combine(fixture.Workspace, "hold-started")));

		Assert.Equal(SessionStatus.Working, fixture.Events.Status.Status);
		Assert.Contains(fixture.Events.Values, value =>
			value is AgentPromptSubmitted { Prompt: "/hold-command" });

		File.WriteAllText(Path.Combine(fixture.Workspace, "release-hold"), string.Empty);
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_QueuesProviderCommandsUntilTheActiveTurnIsIdle() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("hold");
		await Wait.UntilAsync(() => File.Exists(Path.Combine(fixture.Workspace, "hold-started")));
		fixture.SubmitCommand("compact", "/compact");
		File.WriteAllText(Path.Combine(fixture.Workspace, "release-hold"), string.Empty);
		await fixture.WaitForMessageAsync(message => message.Text == "Compacting completed.");

		Assert.False(File.Exists(Path.Combine(fixture.Workspace, "command-steered")));
		Assert.Contains(fixture.Messages, message => message.Text == "steered: released");
	}

	[Fact]
	public async Task NativeSession_SteersPastAQueuedProviderCommand() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("hold");
		await Wait.UntilAsync(() => File.Exists(Path.Combine(fixture.Workspace, "hold-started")));
		fixture.SubmitCommand("compact", "/compact");
		await fixture.WaitForQueueAsync(queued => queued.Count == 1 && queued[0].Text == "/compact");

		fixture.Submit("new direction");

		await fixture.WaitForMessageAsync(message => message.Type == "user-steer" && message.Text == "new direction");
		await fixture.WaitForMessageAsync(message => message.Text == "Compacting completed.");
		await fixture.WaitForQueueAsync(queued => queued.Count == 0);
		Assert.False(File.Exists(Path.Combine(fixture.Workspace, "command-steered")));
	}

	[Fact]
	public async Task NativeSession_RejectsACommandMissingFromTheLatestProviderSnapshot() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("remove-commands");
		await fixture.WaitForControlsAsync(state => state.Slash.All(command => command.Name != "compact"));

		var error = Assert.Throws<InvalidOperationException>(() =>
			fixture.SubmitCommand("compact", "/compact"));

		Assert.Contains("no longer advertises", error.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task NativeSession_StartsAFreshProviderConversationAndResetsLocalState() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("hello");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		Assert.Equal("fake-session", fixture.Sessions.Resolve("fake", fixture.Workspace));
		var oldStarts = fixture.Events.Values
			.OfType<AgentSessionStarted>()
			.ToHashSet(ReferenceEqualityComparer.Instance);

		fixture.Session.StartNewConversation();
		await fixture.WaitForMessageAsync(message => message.Type == "transcript-reset");
		await fixture.Events.WaitForAsync(value => value is AgentSessionStarted started && !oldStarts.Contains(started));
		Assert.Null(fixture.Sessions.Resolve("fake", fixture.Workspace));
		fixture.Submit("context-after-reset");
		var response = await fixture.WaitForMessageAsync(message =>
			message.Text == "context-after-reset:guidance=True;selection=False");

		Assert.Equal("fake-session-2", response.ThreadId);
		Assert.Equal("1", response.TurnId);
		Assert.Equal("fake-session-2", fixture.Sessions.Resolve("fake", fixture.Workspace));
		Assert.Single(fixture.Messages, message => message.Type == "transcript-reset");
	}

	[Fact]
	public async Task NativeSession_PreservesOrderedRichToolContent() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("tool-content");
		var tool = await fixture.WaitForMessageAsync(message => message.ItemId == "tool:content"
			&& message.Type == "item-completed");

		Assert.Collection(tool.Content!,
			content => Assert.Equal(("text", "tool text"), (content.Type, content.Text)),
			content => Assert.Equal(("image", "image/png", "aW1hZ2U="),
				(content.Type, content.MediaType, content.MediaData)),
			content => Assert.Equal(("resource_link", "https://example.test/result", "Result"),
				(content.Type, content.ResourceUri, content.Name)),
			content => Assert.Equal(("resource", "file:///result.txt", "embedded text"),
				(content.Type, content.ResourceUri, content.Text)));
	}

	[Fact]
	public async Task NativeSession_AcceptsEmptyFileWritesAndDiffResults() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		string path = Path.Combine(fixture.Workspace, "empty.txt");

		fixture.Submit("fs-empty:" + path);
		await fixture.WaitForMessageAsync(message => message.Text == "fs: ");
		Assert.Equal(string.Empty, await File.ReadAllTextAsync(path));

		fixture.Submit("empty-diff");
		var tool = await fixture.WaitForMessageAsync(message => message.ItemId == "tool:empty-diff"
			&& message.Type == "item-completed");
		Assert.Equal(string.Empty, Assert.Single(tool.Diffs!).NewText);
	}

	[Fact]
	public async Task NativeSession_SerializesRapidControlMutationsInIssueOrder() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Session.SetControl("model", "beta");
		fixture.Session.SetControl("model", "alpha");
		await fixture.WaitForControlsAsync(state => state.Axes.Any(axis => axis.Id == "model" && axis.Value == "beta"));
		var final = await fixture.WaitForControlsAsync(state =>
			state.Axes.Any(axis => axis.Id == "model" && axis.Value == "alpha"));
		fixture.Submit("control-state");
		await fixture.WaitForMessageAsync(message => message.Text == "control state: alpha/default/False");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal("alpha", Assert.Single(final.Axes, axis => axis.Id == "model").Value);
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_RestartsDuringAControlMutationAndAcceptsFreshControls() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Session.SetControl("model", "beta");
		await Wait.UntilAsync(() => File.Exists(Path.Combine(fixture.Workspace, "control-started")));
		var oldStarts = fixture.Events.Values
			.OfType<AgentSessionStarted>()
			.ToHashSet(ReferenceEqualityComparer.Instance);
		fixture.Session.Restart();
		await fixture.Events.WaitForAsync(value => value is AgentSessionStarted started && !oldStarts.Contains(started));
		fixture.Session.SetControl("model", "beta");
		var controls = await fixture.WaitForControlsAsync(state =>
			state.Axes.Any(axis => axis.Id == "model" && axis.Value == "beta"));
		fixture.Submit("control-state");
		await fixture.WaitForMessageAsync(message => message.Text == "control state: beta/default/False");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal("beta", Assert.Single(controls.Axes, axis => axis.Id == "model").Value);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_RestoresAcceptedControlsBeforeRestartBecomesReady() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Session.SetControl("model", "beta");
		await fixture.WaitForControlsAsync(state =>
			state.Axes.Any(axis => axis.Id == "model" && axis.Value == "beta"));
		var oldStarts = fixture.Events.Values
			.OfType<AgentSessionStarted>()
			.ToHashSet(ReferenceEqualityComparer.Instance);

		fixture.Session.Restart();
		await fixture.Events.WaitForAsync(value => value is AgentSessionStarted started && !oldStarts.Contains(started));
		fixture.Submit("control-state");
		await fixture.WaitForMessageAsync(message => message.Text == "control state: beta/default/False");

		Assert.Equal("beta", Assert.Single(fixture.Session.ControlState.Axes, axis => axis.Id == "model").Value);
	}

	[Fact]
	public async Task NativeSession_DisposeTerminatesANonresponsiveCloseRequest() {
		await using var fixture = AcpAgentSessionFixture.CreateHeldCloseAdapter();
		await fixture.StartAsync();

		await fixture.Session.DisposeAsync();
	}

	[Fact]
	public async Task NativeSession_SurfacesAnAdapterLaunchFailure() {
		var fixture = AcpAgentSessionFixture.CreateNonLaunchingAdapter(out string executable);
		try {
			fixture.Session.Start();
			var error = await fixture.WaitForMessageAsync(message => message.Type == "error");
			await fixture.Events.WaitForAsync(value => value is AgentProcessChanged {
				Change.State: Weavie.Core.Processes.SupervisorState.Idle,
			});

			Assert.Contains("could not start", error.Text, StringComparison.OrdinalIgnoreCase);
			Assert.Single(fixture.Messages, message => message.Type == "error");
			Assert.Single(fixture.Events.Values, value => value is AgentRuntimeFailed);
			Assert.Equal(SessionStatus.Error, fixture.Events.Status.Status);
		} finally {
			await fixture.DisposeAsync();
			File.Delete(executable);
		}
	}

	[Fact]
	public async Task NativeSession_StartsBeforeAnImmediateProtocolFailure() {
		await using var fixture = AcpAgentSessionFixture.CreateImmediatelyMalformedAdapter();
		fixture.Session.Start();
		var error = await fixture.WaitForMessageAsync(message => message.Type == "error");
		await fixture.Events.WaitForAsync(value => value is AgentProcessChanged {
			Change.State: Weavie.Core.Processes.SupervisorState.Idle,
		});

		Assert.Contains("output could not be handled", error.Text, StringComparison.OrdinalIgnoreCase);
		Assert.Single(fixture.Messages, message => message.Type == "error");
		Assert.Single(fixture.Events.Values, value => value is AgentRuntimeFailed);
		Assert.Equal(SessionStatus.Error, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_SteersLiveTurnAndTracksBackgroundSettle() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("hold");
		await fixture.WaitForMessageAsync(message => message.ItemId == "tool:hold" && message.Type == "item-started");
		fixture.Submit("new direction");
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.Text == "steered: new direction");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "1");
		await fixture.WaitForMessageAsync(message => message.Type == "user-steer" && message.Text == "new direction");

		fixture.Submit("background");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "2");
		var subagent = Assert.Single(fixture.Messages, message => message.ItemId == "tool:subagent"
			&& message.Type == "item-started");
		Assert.Equal(SessionStatus.Waiting, fixture.Events.Status.Status);

		fixture.Submit("finish-background");
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.Text == "background finished");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "3");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_AutoAllowsUsingProviderAdvertisedStrongestOption() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("permission");
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.Text == "permission: allow-always");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "1");

		Assert.DoesNotContain(fixture.Messages, message => message.Type == "approval-requested");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_ResolvesManualPermissionsAndTypedInput() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: false, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("permission");
		var approval = await fixture.WaitForMessageAsync(message => message.Type == "approval-requested");
		Assert.Equal(SessionStatus.NeedsInput, fixture.Events.Status.Status);
		fixture.Session.ResolvePermission(approval.RequestId!, "allow-once");
		var resolved = await fixture.WaitForMessageAsync(message => message.Type == "approval-resolved");
		Assert.Equal("allowed once", resolved.Status);
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.Text == "permission: allow-once");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "1");

		fixture.Submit("input");
		var input = await fixture.WaitForMessageAsync(message => message.Type == "input-requested");
		var question = Assert.Single(input.Questions!);
		Assert.Equal("choice", question.Id);
		Assert.Equal(["one", "two"], question.Options.Select(option => option.Value));
		Assert.False(question.AllowsOther);
		fixture.Session.ResolveInput(input.RequestId!, "accept", new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) {
			["choice"] = ["two"],
		});
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.Text == "input: two");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "2");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_ExplicitlyCancelsFormAndUrlElicitations() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: false, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("input-cancel");
		var form = await fixture.WaitForMessageAsync(message => message.Type == "input-requested");
		fixture.Session.ResolveInput(form.RequestId!, "cancel", new Dictionary<string, IReadOnlyList<string>>());
		await fixture.WaitForMessageAsync(message => message.Text == "input action: cancel");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "1");

		fixture.Submit("url-input");
		var url = await fixture.WaitForMessageAsync(message => message.Type == "input-requested"
			&& message.ItemType == "url");
		Assert.Equal("https://example.test/login", url.ResourceUri);
		fixture.Session.ResolveInput(url.RequestId!, "decline", new Dictionary<string, IReadOnlyList<string>>());
		await fixture.WaitForMessageAsync(message => message.Text == "URL action: decline");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "2");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	// The pane keys every item by (threadId, turnId, itemId), so a request and its resolution have to agree on all
	// three. Disagree and the client files the resolution under a key it never looks up for that entry: the card
	// stays pending forever, still rendering live buttons that resolve a request the session already settled.
	[Fact]
	public async Task NativeSession_ResolvesAnInputRequestUnderTheIdentityItAnnounced() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: false, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("input-default-schema");
		var requested = await fixture.WaitForMessageAsync(message => message.Type == "input-requested");
		fixture.Session.ResolveInput(
			requested.RequestId!, "accept", new Dictionary<string, IReadOnlyList<string>>());
		var resolved = await fixture.WaitForMessageAsync(message => message.Type == "input-resolved");

		Assert.Equal(
			(requested.ThreadId, requested.TurnId, requested.ItemId),
			(resolved.ThreadId, resolved.TurnId, resolved.ItemId));
	}

	[Fact]
	public async Task NativeSession_TreatsNullStableAcpFormOptionsAsAbsent() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: false, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("input-null-options");
		var input = await fixture.WaitForMessageAsync(message => message.Type == "input-requested");
		Assert.Equal(2, input.Questions!.Count);
		Assert.All(input.Questions, question => Assert.Empty(question.Options));
		fixture.Session.ResolveInput(input.RequestId!, "accept", new Dictionary<string, IReadOnlyList<string>> {
			["text"] = ["free"],
			["values"] = ["one", "two"],
		});
		await fixture.WaitForMessageAsync(message => message.Text == "null options: free | one,two");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_AcceptsStableAcpFormSchemaDefaults() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: false, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("input-default-schema");
		var input = await fixture.WaitForMessageAsync(message => message.Type == "input-requested");
		Assert.Empty(input.Questions!);
		fixture.Session.ResolveInput(input.RequestId!, "accept", new Dictionary<string, IReadOnlyList<string>>());
		await fixture.WaitForMessageAsync(message => message.Text == "default schema action: accept");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_AcceptsTypeLessTitledMultiSelectItems() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: false, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("input-titled-array");
		var input = await fixture.WaitForMessageAsync(message => message.Type == "input-requested");
		var question = Assert.Single(input.Questions!);
		Assert.Equal(["one", "two"], question.Options.Select(option => option.Value));
		fixture.Session.ResolveInput(input.RequestId!, "accept", new Dictionary<string, IReadOnlyList<string>> {
			["values"] = ["one", "two"],
		});
		await fixture.WaitForMessageAsync(message => message.Text == "titled array: one,two");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_RequiresExplicitConsentAfterAUrlCompletionNotification() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: false, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("url-input");
		var input = await fixture.WaitForMessageAsync(message => message.Type == "input-requested"
			&& message.ItemType == "url");
		fixture.Submit("complete-url");
		await fixture.WaitForMessageAsync(message => message.Text == "URL completion notification sent");
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "input-resolved"
			&& message.ItemId == input.ItemId);
		Assert.Equal(SessionStatus.NeedsInput, fixture.Events.Status.Status);
		fixture.Session.ResolveInput(
			input.RequestId!,
			"decline",
			new Dictionary<string, IReadOnlyList<string>>());
		var resolved = await fixture.WaitForMessageAsync(message => message.Type == "input-resolved"
			&& message.ItemId == input.ItemId);
		await fixture.WaitForMessageAsync(message => message.Text == "URL action: decline");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal("decline", resolved.Status);
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Theory]
	[InlineData("unsafe-url", "absolute HTTP or HTTPS URL")]
	[InlineData("password-input", "password forms are not supported")]
	public async Task NativeSession_RejectsUnsafeElicitationSurfaces(string prompt, string expectedError) {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: false, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit(prompt);
		var error = await fixture.WaitForMessageAsync(message => message.Type == "error"
			&& message.Text?.Contains(expectedError, StringComparison.OrdinalIgnoreCase) == true);

		Assert.Contains(expectedError, error.Text, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "input-requested");
	}

	[Fact]
	public async Task NativeSession_BridgesFileSystemEditorContextAndImages() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		string active = Path.Combine(fixture.Workspace, "active.cs");
		fixture.Editor.SetActive(new ActiveEditor(
			active,
			"csharp",
			"selected",
			new EditorSelection(new EditorPosition(2, 3), new EditorPosition(2, 11), IsEmpty: false)));

		fixture.Submit("context");
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.Text == "context:guidance=True;selection=True");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "1");

		string file = Path.Combine(fixture.Workspace, "written.txt");
		fixture.Submit("fs:" + file);
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.Text == "fs: written through ACP");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "2");
		Assert.Equal("written through ACP", await File.ReadAllTextAsync(file));

		string image = Path.Combine(fixture.Workspace, "image.png");
		await File.WriteAllBytesAsync(image, [1, 2, 3, 4]);
		fixture.Session.Submit(new AgentTurnSubmission {
			Id = "image-submission",
			Text = "image",
			Kind = AgentTurnSubmissionKind.Prompt,
			CommandName = string.Empty,
			Attachments = [new AgentInputAttachment { Id = "image", Path = image, Mime = "image/png" }],
		});
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed" && message.Text == "image=True");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "3");
	}

	[Fact]
	public async Task NativeSession_SettlesAFilesystemMutationWhenTheWriteFails() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("fs:" + fixture.Workspace);
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Single(fixture.Events.Values.OfType<AgentToolStarting>());
		Assert.Single(fixture.Events.Values.OfType<AgentToolCompleted>());
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_TerminalLaunchFailureReturnsToTheAgentWithoutHanging() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("terminal-failure");
		var completed = await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		var error = await fixture.WaitForMessageAsync(message => message.Type == "error");

		Assert.Equal("failed", completed.Status);
		Assert.Contains("weavie-missing-terminal-command", error.Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task NativeSession_CancelsTheExactTerminalWaitAndKeepsTheConnectionUsable() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("terminal-cancel");
		await fixture.WaitForMessageAsync(message => message.Text == "terminal wait cancelled; connection alive");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_CancelsAClientRequestBeforeItsDispatchRuns() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		await File.WriteAllTextAsync(Path.Combine(fixture.Workspace, "missing.txt"), "still connected");

		fixture.Submit("cancel-before-dispatch");
		await fixture.WaitForMessageAsync(message => message.Text ==
			"cancel-before-dispatch handled: still connected");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_DistinguishesConcurrentNumericAndStringRequestIds() {
		await using var fixture = AcpAgentSessionFixture.CreateMixedRequestIdAdapter();
		await File.WriteAllTextAsync(Path.Combine(fixture.Workspace, "mixed-number.txt"), "numeric");
		await File.WriteAllTextAsync(Path.Combine(fixture.Workspace, "mixed-string.txt"), "string");
		await fixture.StartAsync();

		fixture.Submit("mixed");
		await fixture.WaitForMessageAsync(message => message.Text == "mixed ids: numeric | string");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_WaitsForTerminalOutputToDrainAfterExit() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("terminal-output");
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.Text == "terminal: stdout=True;stderr=True;exit=0");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_AcceptsNullStableAcpTerminalOptionals() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("terminal-null-optionals");
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.Text == "terminal: stdout=True;stderr=True;exit=0");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_KeepsRunningWhenAToolEmbedsAnAgentOwnedTerminal() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("agent-terminal");
		var tool = await fixture.WaitForMessageAsync(message => message.ItemId == "tool:agent-exec"
			&& message.Type == "item-completed");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal("agent-owned-terminal", tool.TerminalId);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_MirroredModeIsOneConfigOwnedAxis() {
		await using var fixture = AcpAgentSessionFixture.CreateMirroredModeAdapter();
		var started = await fixture.StartAsync();

		Assert.Equal("default", Assert.Single(started.Axes, axis => axis.Id == "mode").Value);

		fixture.Session.SetControl("mode", "plan");
		var controls = await fixture.WaitForControlsAsync(state =>
			state.Axes.Any(axis => axis.Id == "mode" && axis.Value == "plan"));
		fixture.Submit("control-state");
		await fixture.WaitForMessageAsync(message => message.Text == "control state: alpha/plan/False");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal("plan", Assert.Single(controls.Axes, axis => axis.Id == "mode").Value);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
	}

	[Fact]
	public async Task NativeSession_PromptFailureTerminalizesEveryActiveTool() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("prompt-failure");
		var failed = await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		await fixture.WaitForMessageAsync(message => message.Type == "error");

		Assert.Equal("failed", failed.Status);
		Assert.Contains(fixture.Messages, message => message.ItemId == "tool:foreground-failure"
			&& message.Type == "item-completed" && message.Status == "failed");
		Assert.Contains(fixture.Messages, message => message.ItemId == "tool:subagent"
			&& message.Type == "item-completed" && message.Status == "failed");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_AllowsThoughtAndAnswerChunksToShareAProviderMessageId() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("shared-message-id");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Contains(fixture.Messages, message => message.ItemType == "thought"
			&& message.Type == "item-completed" && message.Text == "deep thought");
		Assert.Contains(fixture.Messages, message => message.ItemType == "agentMessage"
			&& message.Type == "item-completed" && message.Text == "final answer");
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
	}

	[Fact]
	public async Task NativeSession_SeparatesProgressFromOpenablePlanDocuments() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("rich");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		var progress = Assert.Single(fixture.Messages, message => message.ItemType == "progress");
		Assert.Equal("progress:current", progress.ItemId);
		Assert.Contains("[~] Implement", progress.Text, StringComparison.Ordinal);
		Assert.DoesNotContain(fixture.Messages, message => message.ItemType == "plan");

		fixture.Submit("plan-document");
		var plan = await fixture.WaitForMessageAsync(message => message.ItemType == "plan");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "2");

		Assert.Equal("plan:live-plan", plan.ItemId);
		Assert.Equal("# Implementation plan", plan.Text);
	}

	[Fact]
	public async Task NativeSession_RevisesAndRemovesAPlanAtItsOriginalIdentity() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("plan-document");
		var original = await fixture.WaitForMessageAsync(message => message.ItemId == "plan:live-plan");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		fixture.Submit("plan-revision");
		var revised = await fixture.WaitForMessageAsync(message => message.ItemId == "plan:live-plan"
			&& message.Text == "# Revised implementation plan");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "2");
		fixture.Submit("remove-plan");
		var removed = await fixture.WaitForMessageAsync(message => message.Type == "item-retracted"
			&& message.ItemId == "plan:live-plan");

		Assert.Equal(original.TurnId, revised.TurnId);
		Assert.Equal(original.TurnId, removed.TurnId);
	}

	[Fact]
	public async Task NativeSession_PreservesPlanIdentityAcrossResume() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("plan-document");
		var original = await fixture.WaitForMessageAsync(message => message.ItemId == "plan:live-plan");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		var oldStarts = fixture.Events.Values
			.OfType<AgentSessionStarted>()
			.ToHashSet(ReferenceEqualityComparer.Instance);
		fixture.Session.Restart();
		await fixture.Events.WaitForAsync(value => value is AgentSessionStarted started && !oldStarts.Contains(started));
		fixture.Submit("remove-plan");
		var removed = await fixture.WaitForMessageAsync(message => message.Type == "item-retracted"
			&& message.ItemId == "plan:live-plan");

		Assert.Equal(original.TurnId, removed.TurnId);
	}

	[Fact]
	public async Task NativeSession_ReadsEveryAdvertisedPlanDocumentShape() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("item-plan-document");
		var items = await fixture.WaitForMessageAsync(message => message.ItemId == "plan:item-plan");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		fixture.Submit("file-plan-document");
		var file = await fixture.WaitForMessageAsync(message => message.ItemId == "plan:file-plan");

		Assert.Equal("- [x] Inspect\n- [ ] Implement", items.Text);
		Assert.Equal("# File plan", file.Text);
	}

	[Fact]
	public async Task NativeSession_ResolvesARelativeToolLocationInsteadOfFailing() {
		// A relative location used to fault the connection, ending the whole session over a jump link.
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("relative-location");
		var tool = await fixture.WaitForMessageAsync(message => message.ItemId == "tool:relative");

		string resolved = Assert.Single(tool.Locations!).Path;
		Assert.True(Path.IsPathFullyQualified(resolved), resolved);
		Assert.Equal("sample.txt", Path.GetFileName(resolved));
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
	}

	[Fact]
	public async Task NativeSession_ReadsAFilePlanOutsideTheWorkspace() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("external-file-plan-document");
		var plan = await fixture.WaitForMessageAsync(message => message.ItemId == "plan:external-file-plan");

		Assert.Equal("# Outside plan", plan.Text);
	}

	[Fact]
	public async Task NativeSession_RetractsARefusedTurn() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("refusal");
		var completed = await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal("refusal", completed.Status);
		Assert.Contains(fixture.Messages, message => message.Type == "user-message"
			&& !string.IsNullOrEmpty(message.ItemId));
		Assert.Contains(fixture.Messages, message => message.Type == "item-retracted"
			&& message.ItemId == "thought:thought");
		Assert.Contains(fixture.Messages, message => message.Type == "item-retracted"
			&& message.ItemId == "progress:current");
	}

	[Fact]
	public async Task NativeSession_RejectsAMalformedKnownUpdate() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("malformed-update");
		var error = await fixture.WaitForMessageAsync(message => message.Type == "error");

		Assert.Contains("session/update", error.Text, StringComparison.Ordinal);
		Assert.Equal(SessionStatus.Error, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_AcceptsAnInitializeResponseWithoutAgentCapabilities() {
		await using var fixture = AcpAgentSessionFixture.CreateMinimalCapabilitiesAdapter();

		fixture.Session.Start();
		await fixture.Events.WaitForAsync(value => value is AgentSessionStarted);

		fixture.Submit("hello");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
	}

	[Fact]
	public async Task NativeSession_ResetsAnUnresumableTranscriptBeforeCreatingANewSession() {
		await using var fixture = AcpAgentSessionFixture.CreateMinimalCapabilitiesAdapter();
		fixture.Session.Start();
		await fixture.Events.WaitForAsync(value => value is AgentSessionStarted);
		fixture.Submit("hello");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		Assert.NotNull(fixture.Sessions.Resolve("fake", fixture.Workspace));
		var oldStarts = fixture.Events.Values
			.OfType<AgentSessionStarted>()
			.ToHashSet(ReferenceEqualityComparer.Instance);

		fixture.Session.Restart();
		await fixture.WaitForMessageAsync(message => message.Type == "transcript-reset");
		await fixture.Events.WaitForAsync(value => value is AgentSessionStarted started && !oldStarts.Contains(started));

		Assert.Null(fixture.Sessions.Resolve("fake", fixture.Workspace));
		fixture.Submit("context");
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.Text == "context:guidance=False;selection=False");
		Assert.NotNull(fixture.Sessions.Resolve("fake", fixture.Workspace));
	}

	[Fact]
	public async Task NativeSession_ColdResumesWithoutReplayAndContinuesLocalTurnIds() {
		await using var fixture = AcpAgentSessionFixture.CreateResumeOnlyAdapter("resume-session", 8);

		await fixture.StartAsync();
		fixture.Submit("hello");
		var response = await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.Text == "echo: hello");

		Assert.Equal("resume-session", response.ThreadId);
		Assert.Equal("9", response.TurnId);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "transcript-reset");
		Assert.Equal(9, fixture.Sessions.ResolveTurnNumber("fake", fixture.Workspace));
	}

	[Fact]
	public async Task NativeSession_AgentAuthenticationKeepsTheInitializedProcessAndRetriesSetup() {
		await using var fixture = AcpAgentSessionFixture.CreateAgentAuthenticationAdapter();
		fixture.Session.Start();
		var authentication = await fixture.WaitForMessageAsync(message => message.Type == "authentication-requested");

		fixture.Session.Authenticate(
			Assert.IsType<string>(authentication.RequestId),
			"fake-login",
			new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
		await fixture.WaitForControlsAsync(state => state.Axes.Count > 0);

		fixture.Submit("hello");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		Assert.Single(fixture.Messages, message => message.Type == "authentication-requested");
	}

	[Fact]
	public async Task NativeSession_TerminalAuthenticationRunsTheDeclaredInvocationAndRestarts() {
		await using var fixture = AcpAgentSessionFixture.CreateTerminalAuthenticationAdapter();
		fixture.Session.Start();
		var authentication = await fixture.WaitForMessageAsync(message => message.Type == "authentication-requested");

		fixture.Session.Authenticate(
			Assert.IsType<string>(authentication.RequestId),
			"fake-terminal-login",
			new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
		await fixture.WaitForControlsAsync(state => state.Axes.Count > 0);

		var launch = Assert.IsType<AgentLaunch>(fixture.AuthenticationLaunch);
		Assert.Contains("terminal-login", launch.Arguments);
		Assert.Equal("1", launch.Environment["FAKE_LOGIN"]);
		Assert.Equal(AgentExecutableMode.Direct, launch.ExecutableMode);
		fixture.Submit("hello");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		Assert.Single(fixture.Messages, message => message.Type == "authentication-requested");
	}

	[Fact]
	public async Task NativeSession_ReauthenticatesAndRetriesAPromptOnTheSameSession() {
		await using var fixture = AcpAgentSessionFixture.CreateAgentAuthenticationAdapter();
		fixture.Session.Start();
		var authentication = await fixture.WaitForMessageAsync(message => message.Type == "authentication-requested");
		fixture.Session.Authenticate(
			Assert.IsType<string>(authentication.RequestId),
			"fake-login",
			new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
		await fixture.WaitForControlsAsync(state => state.Axes.Count > 0);

		fixture.Submit("auth-expired");
		authentication = await fixture.WaitForMessageAsync(message => message.Type == "authentication-requested"
			&& fixture.Messages.Count(candidate => candidate.Type == "authentication-requested") == 2);
		fixture.Session.Authenticate(
			Assert.IsType<string>(authentication.RequestId),
			"fake-login",
			new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
		var completed = await fixture.WaitForMessageAsync(message => message.Type == "turn-completed"
			&& message.Status == "end_turn");

		Assert.Equal("end_turn", completed.Status);
		Assert.Contains(fixture.Messages, message => message.Type == "item-completed"
			&& message.Text == "echo: auth-expired");
	}

	[Fact]
	public async Task NativeSession_InterruptCancelsTheTurnWithoutReportingAnError() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("hold");
		await fixture.WaitForMessageAsync(message => message.ItemId == "tool:hold" && message.Type == "item-started");
		fixture.Session.Interrupt();
		var completed = await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal("cancelled", completed.Status);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_TerminallyFailsWhenTheAdapterCannotInterruptTheProvider() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("hold-cancel-error");
		await fixture.WaitForMessageAsync(message => message.ItemId == "tool:hold" && message.Type == "item-started");
		fixture.Session.Interrupt();
		var error = await fixture.WaitForMessageAsync(message => message.Type == "error");
		var completed = await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		await fixture.Events.WaitForAsync(value => value is AgentProcessChanged {
			Change.State: Weavie.Core.Processes.SupervisorState.Idle,
		});

		Assert.Contains("ACP agent", error.Text, StringComparison.OrdinalIgnoreCase);
		Assert.Equal("failed", completed.Status);
		Assert.Equal(SessionStatus.Error, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_LoadsTranscriptAndResumesAcrossProcessReplacement() {
		await using var fixture = AcpAgentSessionFixture.Create(
			allowAllPermissions: true,
			persistedSessionId: "replay-session");
		var snapshotTask = fixture.WaitForSnapshotAsync();
		await fixture.StartAsync();
		var snapshot = await snapshotTask;
		Assert.Contains(snapshot, message => message.Type == "user-message" && message.Text == "first persisted prompt");
		Assert.Contains(snapshot, message => message.Type == "user-message" && message.Text == "second persisted prompt");
		Assert.Contains(snapshot, message => message.Type == "item-completed" && message.Text == "persisted transcript");
		var progress = snapshot.Where(message => message.ItemType == "progress").ToArray();
		Assert.Equal(2, progress.Length);
		Assert.Contains(progress, message => message.Text!.Contains("first persisted progress", StringComparison.Ordinal));
		Assert.Contains(progress, message => message.Text!.Contains("second persisted progress", StringComparison.Ordinal));
		var plans = snapshot.Where(message => message.ItemType == "plan").ToArray();
		Assert.Equal(2, plans.Length);
		Assert.Equal(["1", "2"], plans.Select(message => message.TurnId));
		Assert.Contains(plans, message => message.Text == "# First persisted plan");
		Assert.Contains(plans, message => message.Text == "# Second persisted plan");
		// The transcript still calls this one running, but the process that ran it is gone: it replays as an
		// interrupted row, not a spinner nothing will ever resolve.
		Assert.Contains(snapshot, message => message.Type == "item-completed"
			&& message.ItemId == "tool:replayed-background"
			&& message.Status == "cancelled");
		// A tool that finished replays as pending-then-completed. Judging each frame on its own would file the
		// first as interrupted, persisting a second record that contradicts the one that follows it.
		Assert.Equal(
			["completed"],
			snapshot
				.Where(message => message.ItemId == "tool:replayed-finished" && message.Type == "item-completed")
				.Select(message => message.Status));
		// The pane places a record where its stream first appears, so a restore has to arrive in conversation order:
		// every prompt ahead of the work it asked for, and ahead of the turn boundary the pane derives from it.
		Assert.Equal(
			[
				("1", "userMessage:replayed-user-1"),
				("1", "progress:current"),
				("1", "plan:replayed-plan-1"),
				("1", "agentMessage:replayed-agent-1"),
				("2", "userMessage:replayed-user-2"),
				("2", "progress:current"),
				("2", "plan:replayed-plan-2"),
				("2", "agentMessage:replayed-agent-2"),
				("2", "tool:replayed-finished"),
				("2", "tool:replayed-background"),
			],
			snapshot.Select(message => (message.TurnId, message.ItemId)).Distinct());
		Assert.DoesNotContain(snapshot, message => message.Text?.Contains("hidden guidance", StringComparison.Ordinal) == true);
		Assert.DoesNotContain(snapshot, message => message.Text?.Contains("hidden selection", StringComparison.Ordinal) == true);
		Assert.All(snapshot, message => Assert.Equal("replay-session", message.ThreadId));
		// Loading a transcript must not leave the session Waiting on work that died with the previous process:
		// nothing would ever settle it, so it would hold the update drain for the life of the host.
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);

		fixture.Session.Restart();
		await fixture.WaitForControlsAsync(state => state.Axes.Any(axis => axis.Id == "model"));
		fixture.Submit("after restart");
		var response = await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.Text == "echo: after restart");
		Assert.Equal("3", response.TurnId);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
	}

	[Fact]
	public async Task NativeSession_RestartsAnActiveTurnWithoutOldGenerationOutput() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("hold");
		await fixture.WaitForMessageAsync(message => message.ItemId == "tool:hold" && message.Type == "item-started");
		fixture.Session.Restart();
		await fixture.WaitForControlsAsync(state => state.Axes.Any(axis => axis.Id == "model"));
		fixture.Submit("after active restart");
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.Text == "echo: after active restart");
		var completed = await fixture.WaitForMessageAsync(message => message.Type == "turn-completed"
			&& message.Status == "end_turn");

		Assert.Equal("2", completed.TurnId);
		Assert.Single(fixture.Messages, message => message.Type == "turn-completed" && message.TurnId == "1");
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_RestartCannotDeadlockWithAConcurrentProviderCommandUpdate() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("restart-update-race");
		await fixture.WaitForMessageAsync(message => message.ItemId == "tool:restart-update-race"
			&& message.Type == "item-started");

		var block = fixture.Events.BlockNext<AgentTurnStopped>();
		var restart = Task.Run(fixture.Session.Restart);
		await block.Entered.WaitAsync(TimeSpan.FromSeconds(10));
		File.WriteAllText(Path.Combine(fixture.Workspace, "release-restart-update"), string.Empty);
		await Wait.UntilAsync(() => File.Exists(Path.Combine(fixture.Workspace, "restart-update-sent")));
		block.Release();
		await restart.WaitAsync(TimeSpan.FromSeconds(10));
		await fixture.Events.WaitForAsync(value => value is AgentSessionStarted { Source: "restart" });
		var controls = await fixture.WaitForControlsAsync(state => state.Axes.Any(axis => axis.Id == "model"));

		fixture.Submit("after concurrent restart");
		await fixture.WaitForMessageAsync(message => message.Text == "echo: after concurrent restart");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "2");

		Assert.DoesNotContain(controls.Slash, command => command.Name == "stale-command");
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task NativeSession_ProviderCrashIsOneVisibleTerminalFailure() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Submit("crash");
		await fixture.WaitForMessageAsync(message => message.Type == "error");
		await fixture.Events.WaitForAsync(value => value is AgentProcessChanged {
			Change.State: Weavie.Core.Processes.SupervisorState.Idle,
		});

		Assert.Single(fixture.Messages, message => message.Type == "error");
		Assert.Equal(SessionStatus.Error, fixture.Events.Status.Status);
		Assert.Single(fixture.Events.Values, value => value is AgentRuntimeFailed);
	}
}
