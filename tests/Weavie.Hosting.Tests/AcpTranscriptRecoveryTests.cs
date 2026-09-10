using Microsoft.Data.Sqlite;
using Weavie.Core.Agents;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpTranscriptRecoveryTests {
	[Theory]
	[InlineData("", false)]
	[InlineData("turn-completed", false)]
	[InlineData("item-completed", false)]
	[InlineData("turn-completed", true)]
	public async Task FailedConversationResetStopsTheProcessAndPreservesContinuations(string failedMessageType, bool restart) {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("hold");
		await fixture.WaitForMessageAsync(message => message.Type == "item-started" && message.ItemId == "tool:hold");
		fixture.Session.AskAside("hold");
		var side = await fixture.WaitForMessageAsync(message => message.Type == "item-started" && message.ItemId == "tool:hold" && message.ConversationId is not null);
		string parentId = Assert.IsType<string>(fixture.Sessions.Resolve("fake", fixture.Workspace));
		using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Sessions.FilePath, Pooling = false }.ToString());
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = failedMessageType.Length > 0
			? $"CREATE TRIGGER reject_clear BEFORE INSERT ON pane_events WHEN json_extract(NEW.message, '$.Type') = '{failedMessageType}' BEGIN SELECT RAISE(ABORT, 'clear failure'); END"
			: "CREATE TRIGGER reject_clear BEFORE DELETE ON pane_events BEGIN SELECT RAISE(ABORT, 'clear failure'); END";
		command.ExecuteNonQuery();

		Assert.Throws<AcpSessionStoreException>(() => {
			if (restart) fixture.Session.Restart();
			else fixture.Session.StartNewConversation();
		});
		await fixture.Events.WaitForAsync(value => value is AgentProcessChanged { Change.State: Weavie.Core.Processes.SupervisorState.Idle });
		Assert.Equal(SessionStatus.Error, fixture.Events.Status.Status);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "transcript-reset");
		Assert.Equal(parentId, fixture.Sessions.Resolve("fake", fixture.Workspace));
		Assert.Equal(side.ThreadId, Assert.Single(fixture.Sessions.ReadConversations("fake", fixture.Workspace),
			state => state.ConversationId == side.ConversationId).SessionId);
		foreach (string threadId in new[] { parentId, Assert.IsType<string>(side.ThreadId) }) {
			Assert.Contains(fixture.Messages, message => message.ThreadId == threadId
				&& message.Type == "turn-completed" && message.Status == "cancelled");
			Assert.Contains(fixture.Messages, message => message.ThreadId == threadId
				&& message.ItemId == "tool:hold" && message.Type == "item-completed" && message.Status == "cancelled");
		}
		command.CommandText = "DROP TRIGGER reject_clear";
		command.ExecuteNonQuery();

		fixture.Submit("continue original");
		var continued = await fixture.WaitForMessageAsync(message => message.Text == "echo: continue original");
		Assert.Equal(parentId, continued.ThreadId);
		Assert.Equal("2", continued.TurnId);
		fixture.Session.ReplyAside(Assert.IsType<string>(side.ConversationId), "continue side");
		var reply = await fixture.WaitForMessageAsync(message => message.Text == "echo: continue side");
		Assert.Equal(side.ThreadId, reply.ThreadId);
		Assert.Equal("2", reply.TurnId);
		Assert.Single(File.ReadAllLines(Path.Combine(fixture.FakeAcpStateDirectory, "forks.log")));
	}

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
