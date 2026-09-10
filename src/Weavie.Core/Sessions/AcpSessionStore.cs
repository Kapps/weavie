using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Weavie.Core.Agents;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Sessions;

/// <summary>Transactional ACP continuation identities and the display events observed by Weavie.</summary>
public sealed class AcpSessionStore(string path) {
	private static readonly JsonSerializerOptions JsonOptions = new() {
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
	};
	private static readonly JsonSerializerOptions MessageJsonOptions = new(JsonOptions) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
	private readonly Lock _gate = new();
	private bool _schemaReady;

	/// <summary>The private database backing this store.</summary>
	public string FilePath { get; } = Path.GetFullPath(path);

	/// <summary>Returns every continuation descriptor owned by this provider and workspace.</summary>
	public IReadOnlyList<AcpConversationState> ReadConversations(string providerId, string workspace) =>
		Read<AcpConversationState>(providerId, workspace, "SELECT state FROM conversations WHERE owner = $owner");

	/// <summary>Reads the ordered display journal, including side conversations.</summary>
	public IReadOnlyList<AgentPaneMessage> ReadMessages(string providerId, string workspace) =>
		Read<AgentPaneMessage>(providerId, workspace, "SELECT message FROM pane_events WHERE owner = $owner ORDER BY sequence");

	/// <summary>Returns the exact primary provider session, when one has been established.</summary>
	public string? Resolve(string providerId, string workspace) =>
		ReadConversations(providerId, workspace).SingleOrDefault(state => state.ConversationId.Length == 0)?.SessionId;

	/// <summary>Returns the last allocated primary turn.</summary>
	public long ResolveTurnNumber(string providerId, string workspace) =>
		ReadConversations(providerId, workspace).SingleOrDefault(state => state.ConversationId.Length == 0)?.TurnNumber ?? 0;

	/// <summary>Stores a continuation identity without publishing a display event.</summary>
	public void Save(string providerId, string workspace, AcpConversationState state) =>
		Save(providerId, workspace, state, []);

	/// <summary>Commits continuation state and its display events together before they are published.</summary>
	public void Save(string providerId, string workspace, AcpConversationState state, IReadOnlyList<AgentPaneMessage> messages) {
		ArgumentNullException.ThrowIfNull(state);
		ArgumentOutOfRangeException.ThrowIfNegative(state.TurnNumber);
		Execute(connection => {
			using var transaction = connection.BeginTransaction();
			using var command = connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = "INSERT INTO conversations VALUES ($owner, $id, $state) ON CONFLICT(owner, id) DO UPDATE SET state = excluded.state";
			command.Parameters.AddWithValue("$owner", Owner(providerId, workspace));
			command.Parameters.AddWithValue("$id", state.ConversationId);
			command.Parameters.AddWithValue("$state", JsonSerializer.Serialize(state, JsonOptions));
			command.ExecuteNonQuery();
			command.CommandText = "INSERT INTO pane_events(owner, message) VALUES ($owner, $message)";
			var messageParameter = command.Parameters.Add("$message", SqliteType.Text);
			foreach (var message in messages) {
				messageParameter.Value = JsonSerializer.Serialize(message, MessageJsonOptions);
				command.ExecuteNonQuery();
			}
			transaction.Commit();
			return true;
		});
	}

	/// <summary>Atomically discards the primary association, all side identities, and their display history.</summary>
	public void Clear(string providerId, string workspace) => Execute(connection => {
		using var transaction = connection.BeginTransaction();
		using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "DELETE FROM conversations WHERE owner = $owner; DELETE FROM pane_events WHERE owner = $owner";
		command.Parameters.AddWithValue("$owner", Owner(providerId, workspace));
		command.ExecuteNonQuery();
		transaction.Commit();
		return true;
	});

	/// <summary>Deletes display and continuation data for every provider attached to a deleted workspace.</summary>
	public void ClearWorkspace(string workspace) => Execute(connection => {
		using var transaction = connection.BeginTransaction();
		using var command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "DELETE FROM conversations WHERE json_extract(owner, '$[1]') = $cwd; DELETE FROM pane_events WHERE json_extract(owner, '$[1]') = $cwd";
		command.Parameters.AddWithValue("$cwd", NormalizeWorkspace(workspace));
		command.ExecuteNonQuery();
		transaction.Commit();
		return true;
	});

	private IReadOnlyList<T> Read<T>(string providerId, string workspace, string query) => Execute(connection => {
		using var command = connection.CreateCommand();
		command.CommandText = query;
		command.Parameters.AddWithValue("$owner", Owner(providerId, workspace));
		using var reader = command.ExecuteReader();
		var values = new List<T>();
		while (reader.Read()) {
			var value = JsonSerializer.Deserialize<T>(reader.GetString(0), JsonOptions)
				?? throw new JsonException("The saved ACP conversation contains a null record.");
			if (value is AcpConversationState state && (state.ConversationId is null || state.InitialPrompt is null
				|| state.SessionId == string.Empty || state.TurnNumber < 0 || state.AnchorTurnNumber < 0
				|| state.PlanTurns is null || state.PlanTurns.Any(pair => string.IsNullOrEmpty(pair.Key) || string.IsNullOrEmpty(pair.Value)))) {
				throw new JsonException("The saved ACP continuation state is invalid.");
			}
			values.Add(value);
		}
		return values;
	});

	private T Execute<T>(Func<SqliteConnection, T> operation) {
		lock (_gate) {
			try {
				SecureFile.CreateDirectory(Path.GetDirectoryName(FilePath)!);
				using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
					DataSource = FilePath,
					Pooling = false,
				}.ToString());
				connection.Open();
				SecureFile.Restrict(FilePath);
				if (!_schemaReady) {
					using var schema = connection.CreateCommand();
					schema.CommandText = """
						CREATE TABLE IF NOT EXISTS conversations (owner TEXT NOT NULL, id TEXT NOT NULL, state TEXT NOT NULL, PRIMARY KEY(owner, id));
						CREATE TABLE IF NOT EXISTS pane_events (sequence INTEGER PRIMARY KEY AUTOINCREMENT, owner TEXT NOT NULL, message TEXT NOT NULL);
						CREATE INDEX IF NOT EXISTS pane_owner ON pane_events(owner, sequence);
						""";
					schema.ExecuteNonQuery();
					_schemaReady = true;
				}
				return operation(connection);
			} catch (Exception error) when (error is SqliteException or JsonException or IOException or UnauthorizedAccessException) {
				throw new AcpSessionStoreException($"Could not read or save the ACP conversation in '{FilePath}': {error.Message}", error);
			}
		}
	}

	private static string Owner(string providerId, string workspace) {
		ArgumentException.ThrowIfNullOrEmpty(providerId);
		return JsonSerializer.Serialize(new[] { providerId, NormalizeWorkspace(workspace) });
	}

	private static string NormalizeWorkspace(string workspace) {
		ArgumentException.ThrowIfNullOrEmpty(workspace);
		string cwd = PathIdentity.Normalize(workspace);
		return OperatingSystem.IsWindows() ? cwd.ToUpperInvariant() : cwd;
	}
}

/// <summary>Reports a conversation-storage failure without replacing or discarding saved data.</summary>
public sealed class AcpSessionStoreException(string message, Exception innerException) : IOException(message, innerException);
