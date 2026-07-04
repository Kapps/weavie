using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="SessionStore"/> over the in-memory filesystem: a full-overlay save persists the
/// loaded flags + active pointer and reloads them, a null active clears the pointer, an old file lacking the
/// <c>loaded</c> field reads back unloaded, and a malformed file is backed up + reset.
/// </summary>
public sealed class SessionStoreTests {
	private const string StorePath = "/weavie-session-tests/sessions.json";

	private static SessionDescriptor Descriptor(string id, string label, bool loaded) => new() {
		Id = new SessionId(id),
		Label = label,
		WorktreePath = "/wt/" + label,
		IsPrimary = false,
		Loaded = loaded,
	};

	[Fact]
	public void Save_PersistsLoadedFlagsAndActive_AndReloads() {
		var fs = new InMemoryFileSystem();
		var store = new SessionStore(fs, StorePath);

		store.Save([Descriptor("aaaa", "a", loaded: true), Descriptor("bbbb", "b", loaded: false)], new SessionId("aaaa"));

		var reloaded = new SessionStore(fs, StorePath);
		Assert.Equal(2, reloaded.Items.Count);
		Assert.True(reloaded.Items.Single(i => i.Id.Value == "aaaa").Loaded);
		Assert.False(reloaded.Items.Single(i => i.Id.Value == "bbbb").Loaded);
		Assert.Equal(new SessionId("aaaa"), reloaded.ActiveId);
	}

	[Fact]
	public void Save_NullActive_ClearsActive() {
		var fs = new InMemoryFileSystem();
		var store = new SessionStore(fs, StorePath);
		store.Save([Descriptor("aaaa", "a", loaded: true)], new SessionId("aaaa"));

		store.Save([Descriptor("aaaa", "a", loaded: true)], activeId: null);

		Assert.Null(new SessionStore(fs, StorePath).ActiveId);
	}

	[Fact]
	public void Save_ReplacesWholeOverlay() {
		var fs = new InMemoryFileSystem();
		var store = new SessionStore(fs, StorePath);
		store.Save([Descriptor("aaaa", "a", loaded: true), Descriptor("bbbb", "b", loaded: true)], new SessionId("aaaa"));

		store.Save([Descriptor("bbbb", "b", loaded: true)], new SessionId("bbbb"));

		var item = Assert.Single(new SessionStore(fs, StorePath).Items);
		Assert.Equal("b", item.Label);
	}

	[Fact]
	public void OldFileWithoutLoadedField_ReadsBackUnloaded() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(StorePath,
			"""{"version":1,"activeId":null,"sessions":[{"id":"aaaa","label":"a","worktreePath":"/wt/a","isPrimary":false}]}""");

		var store = new SessionStore(fs, StorePath);

		Assert.False(Assert.Single(store.Items).Loaded);
	}

	[Fact]
	public void MalformedFile_BacksUpAndResets() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(StorePath, "{ broken ");

		var store = new SessionStore(fs, StorePath);

		Assert.True(fs.FileExists(StorePath + ".bad"));
		Assert.Empty(store.Items);
	}
}
