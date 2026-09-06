using Microsoft.Data.Sqlite;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Changes;

/// <summary>Transactional records owned by one worktree's review; null values delete records.</summary>
public interface IReviewPersistence {
	/// <summary>Loads the complete committed record set when the review opens.</summary>
	IReadOnlyDictionary<string, string> Read();
	/// <summary>Atomically applies changed records; failure preserves the previous committed state.</summary>
	void Save(IReadOnlyDictionary<string, string?> changes);
}

/// <summary>In-memory transactional review storage for isolated trackers.</summary>
public sealed class MemoryReviewPersistence : IReviewPersistence {
	private readonly Dictionary<string, string> _records = new(StringComparer.Ordinal);
	/// <inheritdoc />
	public IReadOnlyDictionary<string, string> Read() => new Dictionary<string, string>(_records);
	/// <inheritdoc />
	public void Save(IReadOnlyDictionary<string, string?> changes) {
		foreach (var (key, value) in changes) {
			if (value is null) _records.Remove(key);
			else _records[key] = value;
		}
	}
}

/// <summary>SQLite transactions update only changed review records, without rewriting unrelated files.</summary>
public sealed class ReviewPersistence : IReviewPersistence {
	private readonly string _connectionString;
	/// <summary>Opens a worktree's private review database.</summary>
	public ReviewPersistence(string path) {
		SecureFile.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
		_connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
		using var connection = Open();
		SecureFile.Restrict(path);
		using var command = connection.CreateCommand();
		command.CommandText = "CREATE TABLE IF NOT EXISTS records (key TEXT PRIMARY KEY, value TEXT NOT NULL)";
		command.ExecuteNonQuery();
	}
	/// <inheritdoc />
	public IReadOnlyDictionary<string, string> Read() {
		try {
			using var connection = Open();
			using var command = connection.CreateCommand();
			command.CommandText = "SELECT key, value FROM records";
			using var reader = command.ExecuteReader();
			var records = new Dictionary<string, string>(StringComparer.Ordinal);
			while (reader.Read()) records.Add(reader.GetString(0), reader.GetString(1));
			return records;
		} catch (SqliteException error) { throw new IOException("Could not load the saved review.", error); }
	}
	/// <inheritdoc />
	public void Save(IReadOnlyDictionary<string, string?> changes) {
		if (changes.Count == 0) return;
		try {
			using var connection = Open();
			using var transaction = connection.BeginTransaction();
			using var command = connection.CreateCommand();
			command.Transaction = transaction;
			var keyParameter = command.Parameters.Add("$key", SqliteType.Text);
			var valueParameter = command.Parameters.Add("$value", SqliteType.Text);
			foreach (var (key, value) in changes) {
				command.CommandText = value is null ? "DELETE FROM records WHERE key = $key"
					: "INSERT INTO records VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
				keyParameter.Value = key;
				valueParameter.Value = (object?)value ?? DBNull.Value;
				command.ExecuteNonQuery();
			}
			transaction.Commit();
		} catch (SqliteException error) { throw new IOException("Could not save the review. Its changes remain pending.", error); }
	}
	private SqliteConnection Open() {
		var connection = new SqliteConnection(_connectionString);
		try { connection.Open(); return connection; } catch { connection.Dispose(); throw; }
	}
}
