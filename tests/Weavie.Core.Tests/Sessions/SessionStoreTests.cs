using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="SessionStore"/> over the in-memory filesystem: persist + reload (including the
/// active pointer), same-id replacement, removal clearing the active pointer, lookup, and malformed-file
/// backup + reset.
/// </summary>
public sealed class SessionStoreTests {
	private const string StorePath = "/weavie-session-tests/sessions.json";

	private static SessionDescriptor Descriptor(string id, string label, bool primary) => new() {
		Id = new SessionId(id),
		Label = label,
		WorktreePath = "/wt/" + label,
		IsPrimary = primary,
	};

	[Fact]
	public void Add_PersistsAndReloads_WithActive() {
		var fs = new InMemoryFileSystem();
		var store = new SessionStore(fs, StorePath);

		store.Add(Descriptor("aaaa", "main", true));
		store.SetActive(new SessionId("aaaa"));

		var reloaded = new SessionStore(fs, StorePath);
		Assert.Single(reloaded.Items);
		Assert.Equal("main", reloaded.Items[0].Label);
		Assert.Equal(new SessionId("aaaa"), reloaded.ActiveId);
	}

	[Fact]
	public void Add_SameId_Replaces() {
		var fs = new InMemoryFileSystem();
		var store = new SessionStore(fs, StorePath);

		store.Add(Descriptor("aaaa", "main", true));
		store.Add(Descriptor("aaaa", "renamed", true));

		Assert.Single(store.Items);
		Assert.Equal("renamed", store.Items[0].Label);
	}

	[Fact]
	public void Remove_DropsAndClearsActive() {
		var fs = new InMemoryFileSystem();
		var store = new SessionStore(fs, StorePath);
		store.Add(Descriptor("aaaa", "main", true));
		store.SetActive(new SessionId("aaaa"));

		store.Remove(new SessionId("aaaa"));

		Assert.Empty(store.Items);
		Assert.Null(store.ActiveId);
	}

	[Fact]
	public void Get_ReturnsDescriptorOrNull() {
		var fs = new InMemoryFileSystem();
		var store = new SessionStore(fs, StorePath);
		store.Add(Descriptor("aaaa", "main", true));

		Assert.NotNull(store.Get(new SessionId("aaaa")));
		Assert.Null(store.Get(new SessionId("zzzz")));
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
