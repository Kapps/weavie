using Microsoft.Data.Sqlite;
using Weavie.Core.Changes;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class ReviewPersistenceTests : IDisposable {
	private readonly string _directory = Path.Combine(Path.GetTempPath(), "weavie-review-" + Guid.NewGuid().ToString("N"));
	private string Database => Path.Combine(_directory, "state.db");

	[Fact]
	public void FailedBatch_RollsBackEveryRecord_AndReopenPreservesTheCommittedState() {
		var store = new ReviewPersistence(Database);
		store.Save(new Dictionary<string, string?> { ["file:a"] = "original", ["file:b"] = "untouched" });
		using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString())) {
			connection.Open();
			using var command = connection.CreateCommand();
			command.CommandText = "CREATE TRIGGER reject_write BEFORE INSERT ON records WHEN NEW.key = 'fail' BEGIN SELECT RAISE(ABORT, 'test failure'); END";
			command.ExecuteNonQuery();
		}
		Assert.Throws<IOException>(() => store.Save(new Dictionary<string, string?> {
			["file:a"] = "uncommitted",
			["file:b"] = null,
			["fail"] = "rejected",
		}));
		Assert.Equal(new Dictionary<string, string> { ["file:a"] = "original", ["file:b"] = "untouched" }, new ReviewPersistence(Database).Read());

		store.Save(new Dictionary<string, string?> { ["file:a"] = "updated", ["file:b"] = null });
		Assert.Equal(new Dictionary<string, string> { ["file:a"] = "updated" }, new ReviewPersistence(Database).Read());
	}

	public void Dispose() {
		if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
	}
}
