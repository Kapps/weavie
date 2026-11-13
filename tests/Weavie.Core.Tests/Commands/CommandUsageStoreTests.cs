using Weavie.Core.Commands;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class CommandUsageStoreTests {
	private const string StorePath = "/commands/usage.json";
	private static readonly long Week = (long)TimeSpan.FromDays(7).TotalMilliseconds;

	[Fact]
	public void UsageDecaysPerVisitWithoutRevivingOldFrequency() {
		var store = new CommandUsageStore(new InMemoryFileSystem(), StorePath);
		for (int i = 0; i < 16; i++) store.Record("old", "", 0);
		store.Record("recent", "", 8 * Week);
		var scores = store.Snapshot(8 * Week).ToDictionary(entry => entry.Id, entry => entry.Weight);
		Assert.True(scores["recent"] > scores["old"]);
		store.Record("old", "", 8 * Week);
		Assert.Equal(1.0625, store.Snapshot(8 * Week).Single(entry => entry.Id == "old").Weight);
	}

	[Fact]
	public void NormalizedQueryLearningAndResetSurviveReload() {
		var fs = new InMemoryFileSystem();
		var store = new CommandUsageStore(fs, StorePath);
		store.Record("review", " RE ", 0);
		store.Record("review", "re", Week);
		var reloaded = new CommandUsageStore(fs, StorePath);
		Assert.Equal(store.Snapshot(Week), reloaded.Snapshot(Week));
		Assert.Equal(1.5, reloaded.Snapshot(Week).Single(entry => entry.Query == "re").Weight);
		Assert.Equal(2, reloaded.Snapshot(Week).Count);
		reloaded.Clear();
		Assert.Empty(new CommandUsageStore(fs, StorePath).Snapshot(Week));
	}

	[Fact]
	public void InvalidPersistedHistoryFailsVisibly() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(StorePath, "not json");
		Assert.Throws<IOException>(() => new CommandUsageStore(fs, StorePath));
	}
}
