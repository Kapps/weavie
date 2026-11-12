using System.Text.Json;
using Microsoft.Data.Sqlite;
using Weavie.Core.Agents;

namespace Weavie.Core.Sessions;

// Each conversation's blocks hold only their latest state, so they live beside the journal, not in it.
public sealed partial class AcpSessionStore {
	/// <summary>Saves a block's latest state, replacing the previous one.</summary>
	public void SaveBlock(string workspace, AgentBlockState state) {
		ArgumentNullException.ThrowIfNull(state);
		Execute(connection => {
			using var command = BlockCommand(connection, state.ProviderId, workspace,
				"INSERT INTO blocks VALUES ($owner, $conversation, $id, $state) ON CONFLICT(owner, conversation, id) DO UPDATE SET state = excluded.state",
				state.ConversationId, state.Block.Id);
			command.Parameters.AddWithValue("$state", JsonSerializer.Serialize(state.Block, AgentBlockJson.Default.AgentBlock));
			command.ExecuteNonQuery();
			return true;
		});
	}

	/// <summary>Deletes a block whose reference row was retracted.</summary>
	public void DeleteBlock(string providerId, string workspace, string? conversationId, string blockId) => Execute(connection => {
		using var command = BlockCommand(connection, providerId, workspace,
			"DELETE FROM blocks WHERE owner = $owner AND conversation = $conversation AND id = $id", conversationId, blockId);
		command.ExecuteNonQuery();
		return true;
	});

	/// <summary>Reads the blocks the rows folded from <paramref name="messages"/> still show.</summary>
	public IReadOnlyList<AgentBlockState> ReadBlocks(string providerId, string workspace, IReadOnlyList<AgentPaneMessage> messages) {
		var shown = AgentBlockReferences.Of(AgentPaneLedger.Of(messages));
		return Execute(connection => {
			using var command = connection.CreateCommand();
			command.Parameters.AddWithValue("$owner", Owner(providerId, workspace));
			return ReadBlocks(command, providerId).Where(block => shown.Contains(AgentBlockReferences.Key(block))).ToList();
		});
	}

	// Within a Replace: deletes this owner's blocks the new history no longer shows.
	private static void PruneBlocks(SqliteCommand command, string providerId, IReadOnlyList<AgentPaneMessage> messages) {
		var shown = AgentBlockReferences.Of(AgentPaneLedger.Of(messages));
		var orphans = ReadBlocks(command, providerId).Where(block => !shown.Contains(AgentBlockReferences.Key(block))).ToList();
		command.CommandText = "DELETE FROM blocks WHERE owner = $owner AND conversation = $conversation AND id = $id";
		var conversation = command.Parameters.Add("$conversation", SqliteType.Text);
		var id = command.Parameters.Add("$id", SqliteType.Text);
		foreach (var orphan in orphans) {
			conversation.Value = Column(orphan.ConversationId);
			id.Value = orphan.Block.Id;
			command.ExecuteNonQuery();
		}
		command.Parameters.Remove(conversation);
		command.Parameters.Remove(id);
	}

	private static List<AgentBlockState> ReadBlocks(SqliteCommand command, string providerId) {
		command.CommandText = "SELECT conversation, state FROM blocks WHERE owner = $owner";
		var blocks = new List<AgentBlockState>();
		using var reader = command.ExecuteReader();
		while (reader.Read()) {
			var block = JsonSerializer.Deserialize(reader.GetString(1), AgentBlockJson.Default.AgentBlock)
				?? throw new JsonException("The saved block is null.");
			blocks.Add(new(providerId, Conversation(reader.GetString(0)), block));
		}
		return blocks;
	}

	private SqliteCommand BlockCommand(SqliteConnection connection, string providerId, string workspace, string sql, string? conversationId, string blockId) {
		var command = connection.CreateCommand();
		command.CommandText = sql;
		command.Parameters.AddWithValue("$owner", Owner(providerId, workspace));
		command.Parameters.AddWithValue("$conversation", Column(conversationId));
		command.Parameters.AddWithValue("$id", blockId);
		return command;
	}

	// The primary conversation is the empty conversation id on disk, as in the conversations table.
	private static string Column(string? conversationId) => conversationId ?? string.Empty;

	private static string? Conversation(string column) => column.Length > 0 ? column : null;
}
