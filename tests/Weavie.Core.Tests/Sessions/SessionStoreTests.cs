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

	private static SessionDescriptor Descriptor(string id, string label, bool loaded) => new() {
		Id = new SessionId(id),
		Label = label,
		WorktreePath = "/wt/" + label,
		ManagedCheckout = true,
		Loaded = loaded,
		AgentProviderId = "claude",
		EditorSession = EditorSession.Empty,
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

	[Theory]
	[InlineData("{\"version\":3,\"sessions\":[{\"id\":\"a\",\"label\":\"a\",\"worktreePath\":\"/wt/a\",\"managedCheckout\":true,\"loaded\":false,\"editorSession\":{\"open\":[]}}]}")]
	[InlineData("{\"version\":3,\"sessions\":[{\"id\":\"a\",\"label\":\"a\",\"worktreePath\":\"/wt/a\",\"managedCheckout\":true,\"loaded\":false,\"agentProviderId\":\"claude\"}]}")]
	[InlineData("{\"version\":3,\"shellCols\":200,\"shellRows\":50,\"sessions\":[null]}")]
	[InlineData("{\"version\":3,\"sessions\":[{\"id\":\"a\",\"label\":\"a\",\"worktreePath\":\"/wt/a\",\"managedCheckout\":true,\"loaded\":false,\"agentProviderId\":\"claude\",\"editorSession\":{\"open\":null}}]}")]
	[InlineData("{\"version\":3,\"sessions\":[{\"id\":\"a\",\"label\":\"a\",\"worktreePath\":\"/wt/a\",\"managedCheckout\":true,\"loaded\":false,\"agentProviderId\":\"claude\",\"editorSession\":{}}]}")]
	public void IncompleteVersionThreeEntry_BacksUpAndResets(string json) {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(StorePath, json);

		var store = new SessionStore(fs, StorePath);

		Assert.True(fs.FileExists(StorePath + ".bad"));
		Assert.Empty(store.Items);
		Assert.Null(store.ShellSize);
	}
}
