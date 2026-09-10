using Microsoft.Data.Sqlite;
using Weavie.Core.Agents;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpTranscriptRecoveryTests {
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RetryingAnUnreadableDisplayRestoresItsOriginalIdentityAndHistory(bool unreadableMessages) {
		await using var fixture = AcpAgentSessionFixture.CreateResumeOnlyAdapter("saved-session", 8);
		var state = Assert.Single(fixture.Sessions.ReadConversations("fake", fixture.Workspace));
		var saved = new AgentPaneMessage { Type = "user-message", ProviderId = "fake", Text = "saved question" };
		fixture.Sessions.Save("fake", fixture.Workspace, state, [saved]);
		byte[] database = File.ReadAllBytes(fixture.Sessions.FilePath);
		if (unreadableMessages) {
			using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Sessions.FilePath, Pooling = false }.ToString());
			connection.Open();
			using var command = connection.CreateCommand();
			command.CommandText = "UPDATE pane_events SET message = 'broken'";
			command.ExecuteNonQuery();
		} else File.WriteAllText(fixture.Sessions.FilePath, "temporarily unreadable database");
		fixture.Session.Start();
		await fixture.WaitForMessageAsync(message => message.Type == "error");
		File.WriteAllBytes(fixture.Sessions.FilePath, database);

		fixture.Session.Restart();
		Assert.Equal(new[] { saved }, await fixture.WaitForSnapshotAsync());
		fixture.Submit("continued question");
		var response = await fixture.WaitForMessageAsync(message => message.Type == "item-completed" && message.Text == "echo: continued question");

		Assert.Equal("saved-session", response.ThreadId);
		Assert.Equal("9", response.TurnId);
		Assert.Equal("saved-session", fixture.Sessions.Resolve("fake", fixture.Workspace));
		Assert.Contains(fixture.Sessions.ReadMessages("fake", fixture.Workspace), message => message.Text == "saved question");
	}

	[Theory]
	[InlineData(null, false)]
	[InlineData("saved-child", true)]
	public async Task InterruptedOrFailedSideDescriptorsRestoreATerminalCard(string? childId, bool failed) {
		await using var fixture = AcpAgentSessionFixture.CreateResumeOnlyAdapter("saved-session", 2);
		var state = Assert.Single(fixture.Sessions.ReadConversations("fake", fixture.Workspace)) with {
			ConversationId = "btw",
			SessionId = childId,
			AnchorTurnNumber = 2,
			InitialPrompt = "side question",
			TurnNumber = 0,
			Failed = failed,
		};
		fixture.Sessions.Save("fake", fixture.Workspace, state, [new AgentPaneMessage {
			Type = "side-conversation-started", ProviderId = "fake", ConversationId = "btw", Status = "forking",
		}]);

		await fixture.StartAsync();
		var snapshot = await fixture.WaitForSnapshotAsync();

		Assert.Single(snapshot, message => message.Type == "side-conversation-failed" && message.ConversationId == "btw");
		Assert.Throws<InvalidOperationException>(() => fixture.Session.ReplyAside("btw", "follow up"));
		Assert.Single(fixture.Sessions.ReadMessages("fake", fixture.Workspace), message => message.Type == "side-conversation-failed");
	}
}
