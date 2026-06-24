using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// <see cref="RecentFilesStore"/> over the in-memory filesystem: empty-on-missing, record bumps count + recency,
/// frecency ranking (recent-but-rare can beat old-but-frequent), persist + reload, eviction past the cap, and the
/// malformed-file backup + reset recovery contract.
/// </summary>
public sealed class RecentFilesStoreTests {
	private const string Path = "/weavie-recent-tests/recent-files.json";
	private static readonly long Day = TimeSpan.TicksPerDay;

	private static RecentFilesStore NewStore(InMemoryFileSystem fs) => new(fs, Path);

	[Fact]
	public void FreshStore_IsEmptyAndWritesNothing() {
		var fs = new InMemoryFileSystem();
		var store = NewStore(fs);

		Assert.Empty(store.Top(10, 0));
		Assert.False(fs.FileExists(Path));
	}

	[Fact]
	public void Top_RanksMoreRecentFirstWhenCountsEqual() {
		var fs = new InMemoryFileSystem();
		var store = NewStore(fs);
		store.Record("/a.cs", 1 * Day);
		store.Record("/b.cs", 5 * Day);

		Assert.Equal(["/b.cs", "/a.cs"], store.Top(10, 5 * Day));
	}

	[Fact]
	public void Top_RecencyCanOutweighFrequency() {
		var fs = new InMemoryFileSystem();
		var store = NewStore(fs);
		// "/old" was visited many times but long ago; "/fresh" once, just now. With a multi-day half-life the
		// fresh file wins — recency damps the stale file's higher raw count.
		for (int i = 0; i < 5; i++) {
			store.Record("/old.cs", 0);
		}

		store.Record("/fresh.cs", 30 * Day);

		Assert.Equal("/fresh.cs", store.Top(2, 30 * Day)[0]);
	}

	[Fact]
	public void Record_PersistsAndReloads() {
		var fs = new InMemoryFileSystem();
		var store = NewStore(fs);
		store.Record("/a.cs", Day);
		store.Record("/a.cs", 2 * Day);
		store.Record("/b.cs", 3 * Day);
		var before = store.Top(10, 3 * Day);

		// A reloaded store ranks identically — count + last-opened survive the round-trip.
		var reloaded = NewStore(fs);
		Assert.Equal(["/a.cs", "/b.cs"], before);
		Assert.Equal(before, reloaded.Top(10, 3 * Day));
	}

	[Fact]
	public void Top_EvictsLowestFrecencyPastTheCap() {
		var fs = new InMemoryFileSystem();
		var store = NewStore(fs);
		// 250 distinct files at increasing recency; the cap is 200, so the 50 oldest must be dropped.
		for (int i = 0; i < 250; i++) {
			store.Record($"/f{i}.cs", i * Day);
		}

		var top = store.Top(1000, 250 * Day);
		Assert.Equal(200, top.Count);
		Assert.Contains("/f249.cs", top);
		Assert.DoesNotContain("/f0.cs", top);
	}

	[Fact]
	public void MalformedFile_BacksUpAndResets() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(Path, "{ this is not valid json ");

		var store = NewStore(fs);

		Assert.True(fs.FileExists(Path + ".bad"));
		Assert.Empty(store.Top(10, 0));
	}
}
