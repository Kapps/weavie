using Microsoft.Data.Sqlite;
using SQLitePCL;
using Weavie.Core.Workspaces;

namespace Weavie.Core.Changes;

/// <summary>The worktree's saved review checkpoint, separate from its review hints and other session state.</summary>
public sealed class SavedReview(string workspaceRoot) {
	/// <summary>Private directory containing this worktree's review and hints databases.</summary>
	public string DirectoryPath { get; } = Path.Combine(WeaviePaths.WorkspaceDir(WorkspaceId.ForPath(workspaceRoot)), "review");
	private string Database => Path.Combine(DirectoryPath, "review.db");

	/// <summary>Opens the worktree's review record store.</summary>
	public IReviewPersistence Open() => ReadDatabase(() => new ReviewPersistence(Database));

	internal static T ReadDatabase<T>(Func<T> read) {
		try {
			return read();
		} catch (Exception error) when (error.GetBaseException() is SqliteException {
			SqliteErrorCode: raw.SQLITE_CORRUPT or raw.SQLITE_NOTADB
		}) {
			throw new InvalidReviewCheckpointException(error.GetBaseException());
		}
	}

	/// <summary>Resets the confirmed corrupt database under SQLite's own locks, including an unreadable database.</summary>
	public void Clear() {
		using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
			DataSource = Database,
			Mode = SqliteOpenMode.ReadWrite,
			Pooling = false,
		}.ToString());
		connection.Open();
		var db = connection.Handle!;
		SqliteException.ThrowExceptionForRC(raw.sqlite3_db_config(db, raw.SQLITE_DBCONFIG_RESET_DATABASE, 1, out _), db);
		using var command = connection.CreateCommand();
		command.CommandText = "VACUUM";
		command.ExecuteNonQuery();
	}
}

/// <summary>Saved review data failed validation. Opening the session can proceed if the user discards that review.</summary>
public sealed class InvalidReviewCheckpointException(Exception cause)
	: IOException($"Couldn't restore the saved review. {cause.Message}", cause);
