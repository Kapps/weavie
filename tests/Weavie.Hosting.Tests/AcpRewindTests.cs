using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpRewindTests {
	[Fact]
	public async Task RewindContinuesOnABranchHoldingOnlyEarlierTurns() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		Assert.False((await fixture.StartAsync()).Rewindable);
		await SubmitTurnsAsync(fixture, "alpha", "bravo", "charlie");
		await fixture.WaitForControlsAsync(state => state.Rewindable);
		string original = fixture.Sessions.Resolve("fake", fixture.Workspace)!;

		await fixture.Session.RewindBeforeAsync("2");

		var snapshot = await fixture.WaitForSnapshotAsync();
		Assert.Contains(snapshot, message => message.Text == "echo: alpha");
		Assert.DoesNotContain(snapshot, message => message.Text is "bravo" or "echo: bravo" or "charlie");
		Assert.Equal("bravo", (await fixture.WaitForMessageAsync(message => message.Type == "draft")).Text);
		string branch = fixture.Sessions.Resolve("fake", fixture.Workspace)!;
		Assert.StartsWith("fake-fork-", branch, StringComparison.Ordinal);
		Assert.Equal(1, fixture.Sessions.ResolveTurnNumber("fake", fixture.Workspace));

		await SubmitTurnsAsync(fixture, "delta");
		Assert.Equal(["alpha", "delta"], Prompts(fixture, branch));
		Assert.Equal(["alpha", "bravo", "charlie"], Prompts(fixture, original));
		Assert.Contains(fixture.Sessions.ReadMessages("fake", fixture.Workspace), message => message.Type == "turn-started" && message.TurnId == "2");
	}

	[Fact]
	public async Task RewindingTheFirstPromptStartsAFreshConversation() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		await SubmitTurnsAsync(fixture, "alpha");
		string original = fixture.Sessions.Resolve("fake", fixture.Workspace)!;

		await fixture.Session.RewindLatestAsync();

		Assert.Empty(await fixture.WaitForSnapshotAsync());
		Assert.Equal("alpha", (await fixture.WaitForMessageAsync(message => message.Type == "draft")).Text);
		await SubmitTurnsAsync(fixture, "bravo");
		Assert.NotEqual(original, fixture.Sessions.Resolve("fake", fixture.Workspace));
		Assert.Equal(1, fixture.Sessions.ResolveTurnNumber("fake", fixture.Workspace));
		Assert.False(File.Exists(Path.Combine(fixture.FakeAcpStateDirectory, "forks.log")));
	}

	[Fact]
	public async Task AnAgentThatIgnoresTheForkPointLeavesTheConversationUntouched() {
		await using var fixture = AcpAgentSessionFixture.CreateForkIgnoresMessageAdapter();
		await fixture.StartAsync();
		await SubmitTurnsAsync(fixture, "alpha", "bravo");
		string original = fixture.Sessions.Resolve("fake", fixture.Workspace)!;
		var journal = fixture.Sessions.ReadMessages("fake", fixture.Workspace);

		var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.RewindBeforeAsync("2"));

		Assert.Contains("did not rewind", error.Message, StringComparison.Ordinal);
		Assert.Equal(original, fixture.Sessions.Resolve("fake", fixture.Workspace));
		Assert.Equal(journal, fixture.Sessions.ReadMessages("fake", fixture.Workspace));
		fixture.Submit("charlie");
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed" && message.Text == "echo: charlie");
		Assert.Equal(["alpha", "bravo", "charlie"], Prompts(fixture, original));
	}

	[Fact]
	public async Task RewindWaitsForTheAgentToFinish() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		await SubmitTurnsAsync(fixture, "alpha");
		fixture.Submit("hold");
		await fixture.WaitForMessageAsync(message => message.Type == "item-started" && message.ItemId == "tool:hold");

		var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.RewindBeforeAsync("1"));

		Assert.Equal("Wait for the agent to finish before rewinding.", error.Message);
		Assert.Equal(2, fixture.Sessions.ResolveTurnNumber("fake", fixture.Workspace));
	}

	[Fact]
	public async Task ARestartedConversationStaysRewindable() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		await SubmitTurnsAsync(fixture, "alpha");

		fixture.Session.Restart();

		await fixture.WaitForControlsAsync(state => !state.Ready);
		Assert.True((await fixture.WaitForControlsAsync(state => state.Ready)).Rewindable);
	}

	[Fact]
	public async Task AgentsWithoutForkCannotRewind() {
		await using var fixture = AcpAgentSessionFixture.CreateMinimalCapabilitiesAdapter();
		var state = await fixture.StartAsync();

		Assert.False(state.Rewindable);
		await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.RewindLatestAsync());
	}

	private static async Task SubmitTurnsAsync(AcpAgentSessionFixture fixture, params string[] prompts) {
		foreach (string prompt in prompts) {
			fixture.Submit(prompt);
			await fixture.WaitForMessageAsync(message => message.Type == "item-completed" && message.Text == "echo: " + prompt);
		}
	}

	private static string[] Prompts(AcpAgentSessionFixture fixture, string sessionId) =>
		[.. File.ReadAllLines(Path.Combine(fixture.FakeAcpStateDirectory, $"session-transcript-{sessionId}.log"))
			.Select(line => System.Text.Json.JsonDocument.Parse(line).RootElement.EnumerateArray()
				.Last(block => block.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!)];
}
