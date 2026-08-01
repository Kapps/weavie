using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="SessionStore"/> over the in-memory filesystem: a full-overlay save persists the
/// loaded flags and reloads them without persisting client-owned selection, an old file lacking the
/// <c>loaded</c> field reads back unloaded, a malformed file is backed up + reset, and shell size round-trips.
/// </summary>
public sealed class SessionStoreTests {
	private const string StorePath = "/weavie-session-tests/sessions.json";

	private static SessionDescriptor Descriptor(string id, string label, bool loaded) => new() {
		Id = new SessionId(id),
		Label = label,
		WorktreePath = "/wt/" + label,
		IsPrimary = false,
		Loaded = loaded,
		AgentProviderId = "claude",
	};

	[Fact]
	public void Save_PersistsLoadedFlagsWithoutClientSelection_AndReloads() {
		var fs = new InMemoryFileSystem();
		var store = new SessionStore(fs, StorePath);

		store.Save([
			Descriptor("aaaa", "a", loaded: true),
			Descriptor("bbbb", "b", loaded: false) with { AgentProviderId = "codex" },
		]);

		var reloaded = new SessionStore(fs, StorePath);
		Assert.Equal(2, reloaded.Items.Count);
		Assert.True(reloaded.Items.Single(i => i.Id.Value == "aaaa").Loaded);
		Assert.False(reloaded.Items.Single(i => i.Id.Value == "bbbb").Loaded);
		Assert.Equal("codex", reloaded.Items.Single(i => i.Id.Value == "bbbb").AgentProviderId);
		Assert.DoesNotContain("activeId", fs.ReadAllText(StorePath));
	}

	[Fact]
	public void Save_ReplacesWholeOverlay() {
		var fs = new InMemoryFileSystem();
		var store = new SessionStore(fs, StorePath);
		store.Save([Descriptor("aaaa", "a", loaded: true), Descriptor("bbbb", "b", loaded: true)]);

		store.Save([Descriptor("bbbb", "b", loaded: true)]);

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
		Assert.Equal("claude", Assert.Single(store.Items).AgentProviderId);
	}

	[Fact]
	public void RecordShellSize_Flush_PersistsAndReloads() {
		var fs = new InMemoryFileSystem();
		var store = new SessionStore(fs, StorePath);

		store.RecordShellSize(200, 50);
		store.Flush();

		Assert.Equal((200, 50), new SessionStore(fs, StorePath).ShellSize);
	}

	[Fact]
	public void ShellSize_IsNull_WhenNeverRecorded() =>
		Assert.Null(new SessionStore(new InMemoryFileSystem(), StorePath).ShellSize);

	[Fact]
	public void RecordShellSize_SurvivesAnOverlaySave() {
		var fs = new InMemoryFileSystem();
		var store = new SessionStore(fs, StorePath);
		store.RecordShellSize(200, 50);

		store.Save([Descriptor("aaaa", "a", loaded: true)]);

		Assert.Equal((200, 50), new SessionStore(fs, StorePath).ShellSize);
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
