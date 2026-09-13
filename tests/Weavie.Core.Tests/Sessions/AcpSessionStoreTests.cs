using Microsoft.Data.Sqlite;
using Weavie.Core.Agents;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class AcpSessionStoreTests : IDisposable {
	private readonly string _directory = Path.Combine(Path.GetTempPath(), "weavie-transcript-" + Guid.NewGuid().ToString("N"));
	private string Database => Path.Combine(_directory, "conversations.db");

	[Fact]
	public void ReopenPreservesExactConversationsAndDisplayOrderAndClearIsScoped() {
		var store = new AcpSessionStore(Database);
		var primary = State("", "primary-id", 4);
		var side = State("btw-1", "fork-id", 2) with {
			AnchorTurnNumber = 3,
			InitialPrompt = "why?",
			GuidanceSent = true,
			PlanTurns = new Dictionary<string, string> { ["plan"] = "1" },
		};
		var first = Message("user-message", "<context>literal user XML</context>");
		var second = Message("agent-message-delta", "side answer") with { ConversationId = "btw-1", ThreadId = "fork-id" };
		store.Save("provider", "/workspace", primary, [first]);
		store.Save("provider", "/workspace", side, [second]);
		store.Save("other-provider", "/workspace", primary, [first]);
		store.Save("provider", "/another-workspace", primary, [first]);

		var reloaded = new AcpSessionStore(Database);
		Assert.Equal("primary-id", reloaded.Resolve("provider", "/workspace"));
		Assert.Equal(4, reloaded.ResolveTurnNumber("provider", "/workspace"));
		var savedSide = Assert.Single(reloaded.ReadConversations("provider", "/workspace"), state => state.ConversationId == "btw-1");
		Assert.Equal("fork-id", savedSide.SessionId);
		Assert.Equal(2, savedSide.TurnNumber);
		Assert.Equal(3, savedSide.AnchorTurnNumber);
		Assert.True(savedSide.GuidanceSent);
		Assert.Equal("1", savedSide.PlanTurns["plan"]);
		Assert.Equal(new[] { first, second }, reloaded.ReadMessages("provider", "/workspace"));

		reloaded.Clear("provider", "/workspace");
		Assert.Empty(store.ReadConversations("provider", "/workspace"));
		Assert.Empty(store.ReadMessages("provider", "/workspace"));
		Assert.Single(store.ReadMessages("other-provider", "/workspace"));
		store.ClearWorkspace("/workspace");
		Assert.Empty(reloaded.ReadMessages("other-provider", "/workspace"));
		Assert.Empty(reloaded.ReadConversations("other-provider", "/workspace"));
		Assert.Single(reloaded.ReadMessages("provider", "/another-workspace"));
	}

	[Fact]
	public void FailedDisplayWriteRollsBackContinuationStateAndEarlierEventsInTheTransaction() {
		var store = new AcpSessionStore(Database);
		store.Save("provider", "/workspace", State("", "primary-id", 1), [Message("user-message", "saved")]);
		using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString())) {
			connection.Open();
			using var command = connection.CreateCommand();
			command.CommandText = "CREATE TRIGGER reject_event BEFORE INSERT ON pane_events WHEN json_extract(NEW.message, '$.Text') = 'rejected' BEGIN SELECT RAISE(ABORT, 'test failure'); END";
			command.ExecuteNonQuery();
		}
		Assert.Throws<AcpSessionStoreException>(() => store.Save("provider", "/workspace", State("", "primary-id", 2), [
			Message("user-message", "uncommitted"), Message("agent-message-delta", "rejected"),
		]));

		var reloaded = new AcpSessionStore(Database);
		Assert.Equal(1, reloaded.ResolveTurnNumber("provider", "/workspace"));
		Assert.Equal("saved", Assert.Single(reloaded.ReadMessages("provider", "/workspace")).Text);
	}

	[Fact]
	public void CorruptDatabaseFailsWithoutReplacingTheOriginal() {
		Directory.CreateDirectory(_directory);
		File.WriteAllText(Database, "corrupt database");
		var store = new AcpSessionStore(Database);
		var error = Assert.Throws<AcpSessionStoreException>(() => store.ReadMessages("provider", "/workspace"));
		Assert.Contains(Database, error.Message, StringComparison.Ordinal);
		Assert.Equal("corrupt database", File.ReadAllText(Database));
	}

	private static AcpConversationState State(string id, string providerId, long turn) => new() {
		ConversationId = id,
		SessionId = providerId,
		TurnNumber = turn,
		AnchorTurnNumber = 0,
		InitialPrompt = "",
		GuidanceSent = false,
		PlanTurns = new Dictionary<string, string>(),
		Failed = false,
	};

	private static AgentPaneMessage Message(string type, string text) => new() {
		Type = type,
		ProviderId = "provider",
		ThreadId = "primary-id",
		Text = text,
	};

	public void Dispose() {
		if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
	}
}
