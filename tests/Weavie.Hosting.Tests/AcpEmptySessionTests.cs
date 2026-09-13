using Microsoft.Data.Sqlite;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpEmptySessionTests {
	[Theory]
	[InlineData(0, "fake-session")]
	[InlineData(1, "saved-session")]
	public async Task ColdStartResumesOnlyAfterASubmission(long turnNumber, string expectedSessionId) {
		await using var fixture = AcpAgentSessionFixture.CreateResumeOnlyAdapter("saved-session", turnNumber);
		await fixture.StartAsync();

		fixture.Submit("continue");
		var response = await fixture.WaitForMessageAsync(message => message.Text == "echo: continue");

		Assert.Equal(expectedSessionId, response.ThreadId);
		Assert.Equal((turnNumber + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), response.TurnId);
		Assert.DoesNotContain(fixture.Messages, message => message.Type is "error" or "transcript-reset");
	}

	[Fact]
	public async Task EmptyPrimaryRestartsWithoutProviderResumeSupport() {
		await using var fixture = AcpAgentSessionFixture.CreateMinimalCapabilitiesAdapter();
		await fixture.StartAsync();

		fixture.Session.Restart();
		fixture.Submit("first question");
		await fixture.WaitForMessageAsync(message => message.Text == "echo: first question");

		Assert.Equal(1, fixture.Sessions.ResolveTurnNumber("fake", fixture.Workspace));
		Assert.DoesNotContain(fixture.Messages, message => message.Type is "error" or "transcript-reset");
	}

	[Fact]
	public async Task UnpromptedSideForkRetainsItsInheritedConversation() {
		await using var fixture = AcpAgentSessionFixture.CreateFlattenReplayAdapter("saved-primary");
		AcpAgentSessionFixture.SeedSession(fixture.Sessions, "fake", fixture.Workspace, "saved-primary", 1);
		var primary = Assert.Single(fixture.Sessions.ReadConversations("fake", fixture.Workspace));
		fixture.Sessions.Save("fake", fixture.Workspace, primary with {
			ConversationId = "saved-aside",
			SessionId = "saved-child",
			AnchorTurnNumber = 1,
			InitialPrompt = "pending question",
			TurnNumber = 0,
		});
		await fixture.StartAsync();

		fixture.Session.ReplyAside("saved-aside", "continue inherited conversation");
		var response = await fixture.WaitForMessageAsync(message => message.Text == "echo: continue inherited conversation");

		Assert.Equal("saved-child", response.ThreadId);
		Assert.Equal("1", response.TurnId);
		Assert.False(File.Exists(Path.Combine(fixture.FakeAcpStateDirectory, "forks.log")));
	}

	[Fact]
	public async Task FirstSubmissionCanRecoverAnEmptyFailedSessionWithoutResumeSupport() {
		await using var fixture = AcpAgentSessionFixture.CreateMinimalCapabilitiesAdapter();
		await fixture.StartAsync();
		using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
			DataSource = fixture.Sessions.FilePath,
			Pooling = false,
		}.ToString());
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = "CREATE TRIGGER reject_state BEFORE INSERT ON conversations BEGIN SELECT RAISE(ABORT, 'storage unavailable'); END";
		command.ExecuteNonQuery();
		fixture.Session.Restart();
		await fixture.WaitForMessageAsync(message => message.Type == "error");
		command.CommandText = "DROP TRIGGER reject_state";
		command.ExecuteNonQuery();

		fixture.Submit("first question after recovery");
		var response = await fixture.WaitForMessageAsync(message => message.Text == "echo: first question after recovery");

		Assert.Equal("1", response.TurnId);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "transcript-reset");
	}
}
