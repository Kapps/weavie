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
			using var command = connection.CreateCommand();
			command.CommandText = "INSERT INTO blocks VALUES ($owner, $conversation, $id, $state) ON CONFLICT(owner, conversation, id) DO UPDATE SET state = excluded.state";
			command.Parameters.AddWithValue("$owner", Owner(state.ProviderId, workspace));
			command.Parameters.AddWithValue("$conversation", state.ConversationId ?? string.Empty);
			command.Parameters.AddWithValue("$id", state.Block.Id);
			command.Parameters.AddWithValue("$state", JsonSerializer.Serialize(state.Block, AgentBlockJson.Default.AgentBlock));
			command.ExecuteNonQuery();
			return true;
		});
	}

	/// <summary>Reads the blocks <paramref name="messages"/> still show, deleting every other block of this owner.</summary>
	public IReadOnlyList<AgentBlockState> ReadBlocks(string providerId, string workspace, IReadOnlyList<AgentPaneMessage> messages) {
		var references = References(messages);
		return Execute(connection => {
			using var transaction = connection.BeginTransaction();
			using var command = connection.CreateCommand();
			command.Transaction = transaction;
			command.Parameters.AddWithValue("$owner", Owner(providerId, workspace));
			var blocks = new List<AgentBlockState>();
			command.CommandText = "SELECT conversation, state FROM blocks WHERE owner = $owner";
			using (var reader = command.ExecuteReader()) {
				while (reader.Read()) {
					string? conversation = reader.GetString(0) is { Length: > 0 } id ? id : null;
					var block = JsonSerializer.Deserialize(reader.GetString(1), AgentBlockJson.Default.AgentBlock)
						?? throw new JsonException("The saved block is null.");
					blocks.Add(new(providerId, conversation, block));
				}
			}
			var kept = blocks.Where(block => references.Contains((block.ConversationId, block.Block.Id))).ToList();
			Prune(command, references);
			transaction.Commit();
			return kept;
		});
	}

	private static HashSet<(string?, string)> References(IReadOnlyList<AgentPaneMessage> messages) {
		var ledger = AgentPaneLedger.Of(messages);
		return [.. AgentBlockReferences.Of(Enumerable.Range(0, ledger.Count).Select(index => ledger.EntryAt(index).Message))
			.Select(key => (key.ConversationId, key.BlockId))];
	}

	// Deletes this owner's blocks that no kept reference row shows.
	private static void Prune(SqliteCommand command, HashSet<(string?, string)> references) {
		command.CommandText = "SELECT conversation, id FROM blocks WHERE owner = $owner";
		var orphans = new List<(string, string)>();
		using (var reader = command.ExecuteReader()) {
			while (reader.Read()) {
				string conversation = reader.GetString(0);
				string id = reader.GetString(1);
				if (!references.Contains((conversation.Length > 0 ? conversation : null, id))) orphans.Add((conversation, id));
			}
		}
		command.CommandText = "DELETE FROM blocks WHERE owner = $owner AND conversation = $conversation AND id = $id";
		var conversationParameter = command.Parameters.Add("$conversation", SqliteType.Text);
		var idParameter = command.Parameters.Add("$id", SqliteType.Text);
		foreach (var (conversation, id) in orphans) {
			conversationParameter.Value = conversation;
			idParameter.Value = id;
			command.ExecuteNonQuery();
		}
		command.Parameters.Remove(conversationParameter);
		command.Parameters.Remove(idParameter);
	}
}
