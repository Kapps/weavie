using System.Text.Json;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="SessionStore"/> over the in-memory filesystem: a full-overlay save persists the
/// loaded flags and per-session editor state without persisting client-owned selection, a malformed file is
/// backed up + reset, and shell size round-trips.
/// </summary>
public sealed class SessionStoreTests {
	private const string StorePath = "/weavie-session-tests/sessions.json";
	private const string TerminalA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

	private static SessionDescriptor Descriptor(string id, string label, bool loaded) => new() {
		Id = new SessionId(id),
		Label = label,
		WorktreePath = "/wt/" + label,
		Loaded = loaded,
		AgentProviderId = "claude",
		EditorSession = EditorSession.Empty,
		ShellTerminals = [new string(id[0], 32)],
	};

	[Fact]
	public void Save_PersistsLoadedFlagsWithoutClientSelection_AndReloads() {
		var fs = new InMemoryFileSystem();
		var store = new SessionStore(fs, StorePath);

		store.Save([
			Descriptor("aaaa", "a", loaded: true),
			Descriptor("bbbb", "b", loaded: false) with { AgentProviderId = "acp" },
		]);

		var reloaded = new SessionStore(fs, StorePath);
		Assert.Equal(2, reloaded.Items.Count);
		Assert.True(reloaded.Items.Single(i => i.Id.Value == "aaaa").Loaded);
		Assert.False(reloaded.Items.Single(i => i.Id.Value == "bbbb").Loaded);
		Assert.Equal("acp", reloaded.Items.Single(i => i.Id.Value == "bbbb").AgentProviderId);
		Assert.Equal(TerminalA, Assert.Single(reloaded.Items.Single(i => i.Id.Value == "aaaa").ShellTerminals));
		Assert.DoesNotContain("activeId", fs.ReadAllText(StorePath));
	}

	[Fact]
	public void Save_RoundTripsEditorState() {
		var fs = new InMemoryFileSystem();
		using var viewState = JsonDocument.Parse("""{"scrollTop":120}""");
		var descriptor = Descriptor("aaaa", "a", loaded: false) with {
			EditorSession = new EditorSession {
				Active = "/wt/a/file.ts",
				Open = [new EditorSessionEntry {
					Path = "/wt/a/file.ts",
					ViewState = viewState.RootElement.Clone(),
				}],
			},
		};

		new SessionStore(fs, StorePath).Save([descriptor]);

		var editor = Assert.Single(new SessionStore(fs, StorePath).Items).EditorSession;
		Assert.Equal("/wt/a/file.ts", editor.Active);
		Assert.Equal(120, Assert.Single(editor.Open).ViewState!.Value.GetProperty("scrollTop").GetInt32());
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

	[Fact]
	public void SupersededVersion_BacksUpAndResets() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(StorePath, """{"version":2,"sessions":[]}""");

		var store = new SessionStore(fs, StorePath);

		Assert.True(fs.FileExists(StorePath + ".bad"));
		Assert.Empty(store.Items);
	}

	[Fact]
	public void Strict_snapshot_read_rejects_malformed_state_without_repairing_it() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(StorePath, "{ broken ");

		Assert.Throws<JsonException>(() => SessionStore.ReadSnapshot(fs, StorePath));

		Assert.Equal("{ broken ", fs.ReadAllText(StorePath));
		Assert.False(fs.FileExists(StorePath + ".bad"));
	}

	[Fact]
	public void Strict_snapshot_write_round_trips_the_complete_document() {
		var fs = new InMemoryFileSystem();
		SessionStore.WriteSnapshot(fs, StorePath, new SessionStoreSnapshot {
			Items = [Descriptor("aaaa", "a", loaded: true)],
			ShellColumns = 160,
			ShellRows = 48,
		});

		var snapshot = SessionStore.ReadSnapshot(fs, StorePath);

		Assert.Equal("aaaa", Assert.Single(snapshot.Items).Id.Value);
		Assert.Equal(160, snapshot.ShellColumns);
		Assert.Equal(48, snapshot.ShellRows);
	}

	// A file a previous build wrote carries properties the store has since stopped reading; skipping them is
	// what keeps a real session list from being reset on upgrade.
	[Fact]
	public void EntryCarryingAnUnreadProperty_LoadsInsteadOfResetting() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(
			StorePath,
			$$"""{"version":4,"sessions":[{"id":"a","label":"a","worktreePath":"/wt/a","managedCheckout":true,"loaded":true,"agentProviderId":"claude","editorSession":{"open":[]},"shellTerminals":["{{TerminalA}}"],"unread":true}]}""");

		var store = new SessionStore(fs, StorePath);

		Assert.False(fs.FileExists(StorePath + ".bad"));
		Assert.Equal("a", store.Items.Single().Id.Value);
		Assert.True(store.Items.Single().Loaded);
	}

	[Theory]
	[InlineData("{\"version\":4,\"sessions\":[{\"id\":\"a\",\"label\":\"a\",\"worktreePath\":\"/wt/a\",\"managedCheckout\":true,\"loaded\":false,\"editorSession\":{\"open\":[]},\"shellTerminals\":[\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"]}]}")]
	[InlineData("{\"version\":4,\"sessions\":[{\"id\":\"a\",\"label\":\"a\",\"worktreePath\":\"/wt/a\",\"managedCheckout\":true,\"loaded\":false,\"agentProviderId\":\"claude\",\"shellTerminals\":[\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"]}]}")]
	[InlineData("{\"version\":4,\"shellCols\":200,\"shellRows\":50,\"sessions\":[null]}")]
	[InlineData("{\"version\":4,\"sessions\":[{\"id\":\"a\",\"label\":\"a\",\"worktreePath\":\"/wt/a\",\"managedCheckout\":true,\"loaded\":false,\"agentProviderId\":\"claude\",\"editorSession\":{\"open\":null},\"shellTerminals\":[\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"]}]}")]
	[InlineData("{\"version\":4,\"sessions\":[{\"id\":\"a\",\"label\":\"a\",\"worktreePath\":\"/wt/a\",\"managedCheckout\":true,\"loaded\":false,\"agentProviderId\":\"claude\",\"editorSession\":{},\"shellTerminals\":[\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"]}]}")]
	[InlineData("{\"version\":4,\"sessions\":[{\"id\":\"a\",\"label\":\"a\",\"worktreePath\":\"/wt/a\",\"managedCheckout\":true,\"loaded\":false,\"agentProviderId\":\"claude\",\"editorSession\":{\"open\":[]},\"shellTerminals\":[\"\"]}]}")]
	[InlineData("{\"version\":4,\"sessions\":[{\"id\":\"a\",\"label\":\"a\",\"worktreePath\":\"/wt/a\",\"managedCheckout\":true,\"loaded\":false,\"agentProviderId\":\"claude\",\"editorSession\":{\"open\":[]},\"shellTerminals\":[\"../escape\"]}]}")]
	[InlineData("{\"version\":4,\"sessions\":[{\"id\":\"a\",\"label\":\"a\",\"worktreePath\":\"/wt/a\",\"managedCheckout\":true,\"loaded\":false,\"agentProviderId\":\"claude\",\"editorSession\":{\"open\":[]},\"shellTerminals\":[\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"]}]}")]
	public void IncompleteVersionFourEntry_BacksUpAndResets(string json) {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(StorePath, json);

		var store = new SessionStore(fs, StorePath);

		Assert.True(fs.FileExists(StorePath + ".bad"));
		Assert.Empty(store.Items);
		Assert.Null(store.ShellSize);
	}
}
