using Weavie.Core.FileSystem;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// <see cref="RecentWorkspaces"/>: ordering, move-to-front dedupe, persistence, size cap, removal, the
/// Changed event, and malformed-file backup + reset.
/// </summary>
public sealed class RecentWorkspacesTests {
	private const string RecentsPath = "/weavie-recents-tests/recents.json";

	private static RecentWorkspaces NewStore(InMemoryFileSystem fs) => new(fs, RecentsPath);

	private static string Full(string path) => Path.GetFullPath(path);

	[Fact]
	public void FreshStore_IsEmpty() {
		var store = NewStore(new InMemoryFileSystem());
		Assert.Empty(store.Items);
		Assert.Null(store.LastOpened);
	}

	[Fact]
	public void Add_PutsMostRecentFirst() {
		var store = NewStore(new InMemoryFileSystem());
		store.Add("/ws/a");
		store.Add("/ws/b");

		Assert.Equal([Full("/ws/b"), Full("/ws/a")], store.Items);
		Assert.Equal(Full("/ws/b"), store.LastOpened);
	}

	[Fact]
	public void Add_DedupesMovingToFront() {
		var store = NewStore(new InMemoryFileSystem());
		store.Add("/ws/a");
		store.Add("/ws/b");
		store.Add("/ws/a");

		Assert.Equal([Full("/ws/a"), Full("/ws/b")], store.Items);
	}

	[Fact]
	public void Add_PersistsAndReloads() {
		var fs = new InMemoryFileSystem();
		var store = NewStore(fs);
		store.Add("/ws/a");
		store.Add("/ws/b");

		var reloaded = NewStore(fs);
		Assert.Equal([Full("/ws/b"), Full("/ws/a")], reloaded.Items);
	}

	[Fact]
	public void Add_CapsAtTwenty() {
		var store = NewStore(new InMemoryFileSystem());
		for (int i = 0; i < 25; i++) {
			store.Add($"/ws/p{i}");
		}

		Assert.Equal(20, store.Items.Count);
		Assert.Equal(Full("/ws/p24"), store.Items[0]);
		Assert.DoesNotContain(Full("/ws/p0"), store.Items);
	}

	[Fact]
	public void Remove_DropsEntryAndPersists() {
		var fs = new InMemoryFileSystem();
		var store = NewStore(fs);
		store.Add("/ws/a");
		store.Add("/ws/b");
		store.Remove("/ws/a");

		Assert.Equal([Full("/ws/b")], store.Items);
		Assert.Equal([Full("/ws/b")], NewStore(fs).Items);
	}

	[Fact]
	public void Add_RaisesChanged() {
		var store = NewStore(new InMemoryFileSystem());
		bool raised = false;
		store.Changed += () => raised = true;

		store.Add("/ws/a");

		Assert.True(raised);
	}

	[Fact]
	public void MalformedFile_BacksUpAndResets() {
		var fs = new InMemoryFileSystem([new KeyValuePair<string, string>(RecentsPath, "{ not valid json")]);
		var store = NewStore(fs);

		Assert.Empty(store.Items);
		Assert.True(fs.FileExists(RecentsPath + ".bad"));
	}
}
