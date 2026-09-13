using Microsoft.Data.Sqlite;
using Weavie.Core.Agents;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreTranscriptDeletionTests {
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task DeletionCleansUnloadedProviderHistoryAndReportsStorageFailures(bool rejectDelete) {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);
		string workspace = host.Session("feature").WorkspaceRoot;
		var state = new AcpConversationState {
			ConversationId = "",
			SessionId = "old-provider-session",
			AnchorTurnNumber = 0,
			InitialPrompt = "",
			TurnNumber = 1,
			GuidanceSent = true,
			PlanTurns = new Dictionary<string, string>(),
			Failed = false,
		};
		host.AcpSessions.Save("uninstalled-provider", workspace, state, [new AgentPaneMessage {
			Type = "user-message", ProviderId = "uninstalled-provider", Text = "saved conversation",
		}]);
		Assert.True((await host.UnloadSessionAsync("feature")).Ok);
		if (rejectDelete) ExecuteSql(host.AcpSessions.FilePath,
			"CREATE TRIGGER reject_delete BEFORE DELETE ON pane_events BEGIN SELECT RAISE(ABORT, 'cleanup failed'); END");

		var result = await host.DeleteSessionAsync("feature", force: true, classify: false);

		if (rejectDelete) {
			Assert.False(result.Ok);
			Assert.Contains("cleanup failed", result.Error, StringComparison.Ordinal);
			Assert.Single(host.AcpSessions.ReadConversations("uninstalled-provider", workspace));
			ExecuteSql(host.AcpSessions.FilePath, "DROP TRIGGER reject_delete");
			Assert.True((await host.DeleteSessionAsync("feature", force: true, classify: false)).Ok);
		} else Assert.True(result.Ok, result.Error);
		Assert.Empty(host.AcpSessions.ReadConversations("uninstalled-provider", workspace));
		Assert.Empty(host.AcpSessions.ReadMessages("uninstalled-provider", workspace));
	}

	private static void ExecuteSql(string path, string sql) {
		using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
		connection.Open();
		using var command = connection.CreateCommand();
		command.CommandText = sql;
		command.ExecuteNonQuery();
	}
}
