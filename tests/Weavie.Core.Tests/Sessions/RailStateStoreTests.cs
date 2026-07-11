using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="RailStateStore"/> over the in-memory filesystem: defaults, last-location + promoted-set
/// persistence across reloads, dedup, no-op-on-unchanged (no rewrite, no event), change notifications, and
/// malformed-file backup + reset.
/// </summary>
public sealed class RailStateStoreTests {
	private const string StorePath = "/weavie-rail-state-tests/rail-state.json";

	[Fact]
	public void Defaults_LocalAndEmpty() {
		var store = new RailStateStore(new InMemoryFileSystem(), StorePath);

		Assert.Equal("local", store.LastLocation);
		Assert.Equal("claude", store.LastAgentProvider);
		Assert.Empty(store.Promoted);
	}

	[Fact]
	public void SetLastAgentProvider_PersistsAcrossReload() {
		var fs = new InMemoryFileSystem();
		new RailStateStore(fs, StorePath).SetLastAgentProvider("codex");

		Assert.Equal("codex", new RailStateStore(fs, StorePath).LastAgentProvider);
	}

	[Fact]
	public void SetLastAgentProvider_InvalidFallsBackToClaude() {
		var store = new RailStateStore(new InMemoryFileSystem(), StorePath);
		store.SetLastAgentProvider("codex");

		store.SetLastAgentProvider("unknown");

		Assert.Equal("claude", store.LastAgentProvider);
	}

	[Fact]
	public void SetLastLocation_PersistsAcrossReload() {
		var fs = new InMemoryFileSystem();
		new RailStateStore(fs, StorePath).SetLastLocation("remote:devbox");

		Assert.Equal("remote:devbox", new RailStateStore(fs, StorePath).LastLocation);
	}

	[Fact]
	public void SetLastLocation_BlankFallsBackToLocal() {
		var store = new RailStateStore(new InMemoryFileSystem(), StorePath);
		store.SetLastLocation("remote:devbox");

		store.SetLastLocation("   ");

		Assert.Equal("local", store.LastLocation);
	}

	[Fact]
	public void SetPromoted_PersistsAndDedups() {
		var fs = new InMemoryFileSystem();
		new RailStateStore(fs, StorePath).SetPromoted(["remote:devbox s1", "remote:devbox s1", "remote:devbox s2"]);

		var reloaded = new RailStateStore(fs, StorePath);

		Assert.Equal(2, reloaded.Promoted.Count);
		Assert.Contains("remote:devbox s1", reloaded.Promoted);
		Assert.Contains("remote:devbox s2", reloaded.Promoted);
	}

	[Fact]
	public void SetLastLocation_Unchanged_DoesNotRewriteOrNotify() {
		var fs = new InMemoryFileSystem();
		var store = new RailStateStore(fs, StorePath);
		store.SetLastLocation("remote:devbox");
		string afterFirst = fs.ReadAllText(StorePath);
		int changes = 0;
		store.Changed += () => changes++;

		store.SetLastLocation("remote:devbox");

		Assert.Equal(afterFirst, fs.ReadAllText(StorePath)); // identical bytes — no rewrite
		Assert.Equal(0, changes);
	}

	[Fact]
	public void SetPromoted_SameSetDifferentOrder_DoesNotNotify() {
		var store = new RailStateStore(new InMemoryFileSystem(), StorePath);
		store.SetPromoted(["a", "b"]);
		int changes = 0;
		store.Changed += () => changes++;

		store.SetPromoted(["b", "a"]); // same set, reordered

		Assert.Equal(0, changes);
	}

	[Fact]
	public void SetPromoted_SameCountDifferentMembers_PersistsAndNotifies() {
		// Equal count is not equal membership: swapping one key for another (same size) is a real change that
		// must persist and notify, not be mistaken for a no-op.
		var store = new RailStateStore(new InMemoryFileSystem(), StorePath);
		store.SetPromoted(["a", "b"]);
		int changes = 0;
		store.Changed += () => changes++;

		store.SetPromoted(["a", "c"]); // same count, different member

		Assert.Equal(1, changes);
		Assert.Contains("c", store.Promoted);
		Assert.DoesNotContain("b", store.Promoted);
	}

	[Fact]
	public void Changed_FiresOnRealChange() {
		var store = new RailStateStore(new InMemoryFileSystem(), StorePath);
		int changes = 0;
		store.Changed += () => changes++;

		store.SetLastLocation("remote:devbox");
		store.SetPromoted(["remote:devbox s1"]);

		Assert.Equal(2, changes);
	}

	[Fact]
	public void MalformedFile_BacksUpAndResets() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(StorePath, "{ broken ");

		var store = new RailStateStore(fs, StorePath);

		Assert.True(fs.FileExists(StorePath + ".bad"));
		Assert.Equal("local", store.LastLocation);
		Assert.Equal("claude", store.LastAgentProvider);
		Assert.Empty(store.Promoted);
	}
}
