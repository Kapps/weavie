using Weavie.Core.Agents;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpOwnedConversationTests {
	[Fact]
	public async Task FreshHandleOwnsItsViewAndClosesWithoutEndingMainOrSibling() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		await using var first = await fixture.Session.OpenFreshAsync(Binding(fixture), CancellationToken.None);
		await using var second = await fixture.Session.OpenFreshAsync(Binding(fixture), CancellationToken.None);
		first.Submit(AgentTurnSubmission.Prompt("first child"));
		await fixture.WaitForMessageAsync(message => message.ConversationId == first.Id && message.Type == "turn-completed");
		Assert.Contains(first.View.Rows, message => message.Text == "echo: first child");
		Assert.DoesNotContain(second.View.Rows, message => message.Text == "echo: first child");
		Assert.False(first.Completion.IsCompleted);

		await first.DisposeAsync();
		await first.DisposeAsync();
		Assert.Equal("closed", (await first.Completion).Reason);
		Assert.Throws<InvalidOperationException>(() => first.Submit(AgentTurnSubmission.Prompt("late input")));
		second.Submit(AgentTurnSubmission.Prompt("second child"));
		fixture.Submit("main survives");
		await fixture.WaitForMessageAsync(message => message.ConversationId == second.Id && message.Text == "echo: second child");
		await fixture.WaitForMessageAsync(message => message.ConversationId is null && message.Text == "echo: main survives");
		Assert.Single(File.ReadAllLines(Path.Combine(fixture.FakeAcpStateDirectory, "closes.log")));
		Assert.DoesNotContain(fixture.Sessions.ReadConversations("fake", fixture.Workspace), state => state.ConversationId == first.Id);
	}

	[Fact]
	public async Task RequiredControlFailureEndsOnlyItsOpeningConversation() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		var hold = new TestControlHold();
		hold.Held["model"] = "missing-model";
		await Assert.ThrowsAsync<AgentControlException>(() => fixture.Session.OpenFreshAsync(Binding(fixture) with { ControlHold = hold }, CancellationToken.None));
		fixture.Submit("main survives opening failure");
		await fixture.WaitForMessageAsync(message => message.ConversationId is null && message.Text == "echo: main survives opening failure");
		Assert.True(fixture.Session.ControlState.Ready);
	}

	[Fact]
	public async Task OpeningWaitsForAuthenticationAndCancellationLeavesMainAvailable() {
		await using var fixture = AcpAgentSessionFixture.CreateSideHeldAuthenticationAdapter();
		await fixture.StartAsync();
		fixture.Submit("fork context");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		using var cancellation = new CancellationTokenSource();
		var opening = fixture.Session.OpenForkAsync(Binding(fixture), cancellation.Token);
		await fixture.WaitForMessageAsync(message => message.Type == "authentication-requested" && message.ConversationId is not null);
		Assert.False(opening.IsCompleted);
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
		string fork = File.ReadAllLines(Path.Combine(fixture.FakeAcpStateDirectory, "forks.log"))[0].Split("->", StringSplitOptions.None)[1];
		await ReadFileAsync(Path.Combine(fixture.FakeAcpStateDirectory, "closes.log"), value => value.Contains(fork, StringComparison.Ordinal));
		fixture.Submit("main while child signs in");
		await fixture.WaitForMessageAsync(message => message.ConversationId is null && message.Text == "echo: main while child signs in");
	}

	[Fact]
	public async Task PromptCompletionDoesNotSettleUnacknowledgedSteering() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		await using var child = await fixture.Session.OpenFreshAsync(Binding(fixture), CancellationToken.None);
		child.Submit(AgentTurnSubmission.Prompt("hold"));
		await fixture.WaitForHeldToolAsync(message => message.ConversationId == child.Id);
		child.Submit(AgentTurnSubmission.Prompt("held-steering"));
		await ReadFileAsync(Path.Combine(fixture.Workspace, "steering-started"), _ => true);
		child.Interrupt();
		await fixture.WaitForMessageAsync(message => message.ConversationId == child.Id && message.Type == "turn-completed");
		Assert.False(child.Settled);
		File.WriteAllText(Path.Combine(fixture.Workspace, "release-steering"), string.Empty);
		await fixture.WaitForMessageAsync(message => message.ConversationId == child.Id && message.Type == "turn-completed" && message.TurnId == "2");
		Assert.False(child.Completion.IsCompleted);
	}

	[Fact]
	public async Task CancellingForkRetiresItsEndpointBeforeTheWireOpenCompletes() {
		await using var fixture = AcpAgentSessionFixture.CreateHeldForkAdapter(authenticationRequired: false);
		await fixture.StartAsync();
		fixture.Submit("fork context");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		using var cancellation = new CancellationTokenSource();
		var opening = fixture.Session.OpenForkAsync(Binding(fixture), cancellation.Token);
		string fork = await ReadFileAsync(Path.Combine(fixture.Workspace, "fork-started"), value => value.StartsWith("fake-fork-", StringComparison.Ordinal));
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
		File.WriteAllText(Path.Combine(fixture.Workspace, "release-fork"), string.Empty);
		await ReadFileAsync(Path.Combine(fixture.FakeAcpStateDirectory, "closes.log"), value => value.Contains(fork, StringComparison.Ordinal));
		Assert.DoesNotContain(fixture.Messages, message => message.Text == "early fork update");
		fixture.Submit("main after cancelled fork");
		await fixture.WaitForMessageAsync(message => message.Text == "echo: main after cancelled fork");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ReplacingMainEndsOwnedHandlesWithoutRetargetingTheirInput(bool clear) {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		await using var child = await fixture.Session.OpenFreshAsync(Binding(fixture), CancellationToken.None);
		child.Submit(AgentTurnSubmission.Prompt("hold"));
		await fixture.WaitForHeldToolAsync(message => message.ConversationId == child.Id);
		if (clear) fixture.Session.StartNewConversation();
		else fixture.Session.Restart();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		await child.Completion.WaitAsync(timeout.Token);
		Assert.Throws<InvalidOperationException>(() => child.Submit(AgentTurnSubmission.Prompt("stale child")));
		fixture.Submit("successor");
		await fixture.WaitForMessageAsync(message => message.ConversationId is null && message.Text == "echo: successor");
		Assert.DoesNotContain(fixture.Messages, message => message.Text == "stale child");
	}

	[Fact]
	public async Task ForkKeepsItsCredentialSeparateFromTheParent() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("fork context");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		await using var child = await fixture.Session.OpenForkAsync(Binding(fixture), CancellationToken.None);
		child.Submit(AgentTurnSubmission.Prompt("mcp:listSettings:{}"));
		await fixture.WaitForMessageAsync(message => message.ConversationId == child.Id && message.Text?.StartsWith("listSettings:", StringComparison.Ordinal) == true);
		await child.DisposeAsync();
		fixture.Submit("mcp:listSettings:{}");
		await fixture.WaitForMessageAsync(message => message.ConversationId is null && message.Text?.StartsWith("listSettings:", StringComparison.Ordinal) == true);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task OpeningAtStartupWaitsForItsCapturedMainConversation(bool fork) {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		fixture.ControlHold.Held["model"] = "beta";
		File.WriteAllText(Path.Combine(fixture.Workspace, "hold-open"), string.Empty);
		fixture.Start();
		await Wait.UntilAsync(() => File.Exists(Path.Combine(fixture.Workspace, "open-started")));
		var opening = fork
			? fixture.Session.OpenForkAsync(Binding(fixture), CancellationToken.None)
			: fixture.Session.OpenFreshAsync(Binding(fixture), CancellationToken.None);
		Assert.False(opening.IsCompleted);
		File.WriteAllText(Path.Combine(fixture.Workspace, "release-open"), string.Empty);
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		await using var child = await opening.WaitAsync(timeout.Token);
		Assert.Equal("beta", Assert.Single(child.ControlState.Axes, axis => axis.Id == "model").Value);
		Assert.Empty(fixture.ControlDefaults.Resolve("fake"));
		child.Submit(AgentTurnSubmission.Prompt("startup stage"));
		await fixture.WaitForMessageAsync(message => message.ConversationId == child.Id && message.Text == "echo: startup stage");
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public async Task ReplacingMainWhileOpeningWaitsDoesNotRetargetTheSuccessor(bool fork, bool clear) {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		File.WriteAllText(Path.Combine(fixture.Workspace, "hold-open"), string.Empty);
		fixture.Start();
		await Wait.UntilAsync(() => File.Exists(Path.Combine(fixture.Workspace, "open-started")));
		var opening = fork
			? fixture.Session.OpenForkAsync(Binding(fixture), CancellationToken.None)
			: fixture.Session.OpenFreshAsync(Binding(fixture), CancellationToken.None);
		Assert.False(opening.IsCompleted);
		if (clear) fixture.Session.StartNewConversation();
		else fixture.Session.Restart();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		await Assert.ThrowsAsync<ObjectDisposedException>(() => opening.WaitAsync(timeout.Token));
		await fixture.WaitForControlsAsync(state => state.Ready);
		fixture.Submit("successor after pending stage");
		await fixture.WaitForMessageAsync(message => message.ConversationId is null && message.Text == "echo: successor after pending stage");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task SameRuntimeCapturesParentControlsAndOverlaysExplicitRequirements(bool fork) {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		await fixture.Session.ApplyControlsAsync([new("model", "beta"), new("fast", "true")]);
		fixture.Submit("fork controls");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		var hold = new TestControlHold();
		hold.Held["model"] = "alpha";
		var binding = Binding(fixture) with { ControlHold = hold };
		await using var child = await (fork
			? fixture.Session.OpenForkAsync(binding, CancellationToken.None)
			: fixture.Session.OpenFreshAsync(binding, CancellationToken.None));
		Assert.Equal("alpha", Assert.Single(child.ControlState.Axes, axis => axis.Id == "model").Value);
		Assert.Equal("true", Assert.Single(child.ControlState.Axes, axis => axis.Id == "fast").Value);
		Assert.Empty(fixture.ControlDefaults.Resolve("fake"));
		await fixture.Session.ApplyControlsAsync([new("fast", "false")]);
		Assert.Equal("true", Assert.Single(child.ControlState.Axes, axis => axis.Id == "fast").Value);
	}

	[Fact]
	public async Task AKeptConversationIsRestoredOnlyByItsOwnerAfterARestartAndForgottenWhenClosed() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		var key = new AgentConversationKey("r-1", "review");
		var child = await fixture.Session.OpenFreshAsync(Binding(fixture) with { Owner = key }, CancellationToken.None);
		child.Submit(AgentTurnSubmission.Prompt("before restart"));
		var first = await fixture.WaitForMessageAsync(message => message.ConversationId == child.Id && message.Text == "echo: before restart");
		Assert.Contains(fixture.Sessions.ReadConversations("fake", fixture.Workspace), state => state.ConversationId == child.Id && state.Owner == key);
		Assert.DoesNotContain(fixture.Session.Asides, aside => aside.Id == child.Id);

		fixture.Session.Restart();
		Assert.Equal("closed", (await child.Completion).Reason);
		Assert.Throws<InvalidOperationException>(() => fixture.Session.ReplyAside(child.Id, AgentTurnSubmission.Prompt("as an aside")));
		Assert.Null(await fixture.Session.RestoreAsync(Binding(fixture) with { Owner = key with { Name = "other" } }, CancellationToken.None));
		await using var restored = (await fixture.Session.RestoreAsync(Binding(fixture) with { Owner = key }, CancellationToken.None))!;
		Assert.Equal(child.Id, restored.Id);
		restored.Submit(AgentTurnSubmission.Prompt("after restart"));
		var second = await fixture.WaitForMessageAsync(message => message.ConversationId == child.Id && message.Text == "echo: after restart");
		Assert.Equal(first.ThreadId, second.ThreadId);
		Assert.Single(fixture.Messages, message => message.Type == "side-conversation-started" && message.ConversationId == child.Id);

		await restored.DisposeAsync();
		Assert.Contains(fixture.Messages, message => message.Type == "side-conversation-closed" && message.ConversationId == child.Id);
		Assert.DoesNotContain(fixture.Sessions.ReadConversations("fake", fixture.Workspace), state => state.ConversationId == child.Id);
		Assert.Null(await fixture.Session.RestoreAsync(Binding(fixture) with { Owner = key }, CancellationToken.None));
	}

	[Fact]
	public async Task ForgettingAnOwnerEndsTheCardOfAKeptConversationThatIsNotLive() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		var kept = new AgentConversationKey("r-1", "review");
		var child = await fixture.Session.OpenFreshAsync(Binding(fixture) with { Owner = kept }, CancellationToken.None);
		var live = await fixture.Session.OpenFreshAsync(Binding(fixture) with { Owner = kept with { Name = "verify" } }, CancellationToken.None);
		child.Submit(AgentTurnSubmission.Prompt("kept"));
		await fixture.WaitForMessageAsync(message => message.ConversationId == child.Id && message.Type == "turn-completed");
		fixture.Session.Restart();
		await child.Completion;
		await using var restored = (await fixture.Session.RestoreAsync(Binding(fixture) with { Owner = kept with { Name = "verify" } }, CancellationToken.None))!;
		fixture.Session.Forget(owner => owner.Scope == "r-1");
		Assert.Contains(fixture.Messages, message => message.Type == "side-conversation-closed" && message.ConversationId == child.Id);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "side-conversation-closed" && message.ConversationId == restored.Id);
		Assert.Equal([restored.Id], fixture.Sessions.ReadConversations("fake", fixture.Workspace).Where(state => state.Owner is not null).Select(state => state.ConversationId));
		await live.DisposeAsync();
	}

	private static AgentConversationBinding Binding(AcpAgentSessionFixture fixture) => new() {
		Tools = fixture.Registry.CreateGrant(() => null),
		Title = "Owned conversation",
		TurnContext = AgentTurnContext.None,
		ControlHold = AgentControlHold.None,
	};

	private static async Task<string> ReadFileAsync(string path, Func<string, bool> accept) {
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		while (true) {
			if (File.Exists(path) && File.ReadAllText(path) is { } text && accept(text)) return text.Trim();
			await Task.Delay(10, timeout.Token);
		}
	}
}
