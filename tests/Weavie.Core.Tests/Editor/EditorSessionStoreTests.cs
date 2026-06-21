using System.Text.Json;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// <see cref="EditorSessionStore"/> over the in-memory filesystem: empty-on-missing, persist + reload (opaque
/// view-state round-trip), malformed-file backup + reset, and the restore push listing open files (no content)
/// while skipping and de-activating files that no longer exist.
/// </summary>
public sealed class EditorSessionStoreTests {
	private const string SessionPath = "/weavie-editor-tests/editor-session.json";
	private const string FilePath = "/weavie-editor-tests/file.ts";

	private static EditorSessionStore NewStore(InMemoryFileSystem fs) => new(fs, SessionPath);

	[Fact]
	public void FreshStore_IsEmptyAndWritesNothing() {
		var fs = new InMemoryFileSystem();
		var store = NewStore(fs);

		Assert.Null(store.Current.Active);
		Assert.Empty(store.Current.Open);
		Assert.False(fs.FileExists(SessionPath));
	}

	[Fact]
	public void Update_PersistsAndReloadsWithOpaqueViewState() {
		var fs = new InMemoryFileSystem();
		var store = NewStore(fs);
		EditorSession? seen = null;
		store.Changed += s => seen = s;

		using var viewStateDoc = JsonDocument.Parse("""{"scrollTop":120,"cursor":{"line":7}}""");
		var session = new EditorSession {
			Active = FilePath,
			Open = [new EditorSessionEntry { Path = FilePath, ViewState = viewStateDoc.RootElement.Clone() }],
		};
		store.Update(session);

		Assert.NotNull(seen);
		Assert.Equal(FilePath, seen!.Active);

		var reloaded = NewStore(fs);
		Assert.Equal(FilePath, reloaded.Current.Active);
		var entry = Assert.Single(reloaded.Current.Open);
		Assert.Equal(FilePath, entry.Path);
		Assert.Equal(120, entry.ViewState!.Value.GetProperty("scrollTop").GetInt32());
	}

	[Fact]
	public void MalformedFile_BacksUpAndResets() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(SessionPath, "{ this is not valid json ");

		var store = NewStore(fs);

		Assert.True(fs.FileExists(SessionPath + ".bad"));
		Assert.Null(store.Current.Active);
		Assert.Empty(store.Current.Open);
	}

	[Fact]
	public void BuildRestoreJson_ListsExistingFilesWithoutContent() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(FilePath, "export const x = 1;\n");
		var store = NewStore(fs);
		using var viewStateDoc = JsonDocument.Parse("""{"scrollTop":42}""");
		store.Update(new EditorSession {
			Active = FilePath,
			Open = [new EditorSessionEntry { Path = FilePath, ViewState = viewStateDoc.RootElement.Clone() }],
		});

		using var message = JsonDocument.Parse(store.BuildRestoreJson());
		var session = message.RootElement.GetProperty("session");

		Assert.Equal("set-editor-session", message.RootElement.GetProperty("type").GetString());
		Assert.Equal(FilePath, session.GetProperty("active").GetString());
		var entry = session.GetProperty("open").EnumerateArray().Single();
		Assert.Equal(FilePath, entry.GetProperty("path").GetString());
		Assert.Equal(42, entry.GetProperty("viewState").GetProperty("scrollTop").GetInt32());
		// Disk is the source of truth; the restore push carries no file content.
		Assert.False(entry.TryGetProperty("content", out _));
	}

	[Fact]
	public void BuildRestoreJson_SkipsMissingFileAndNullsActive() {
		var fs = new InMemoryFileSystem();
		var store = NewStore(fs);
		store.Update(new EditorSession {
			Active = FilePath,
			Open = [new EditorSessionEntry { Path = FilePath }],
		});

		using var message = JsonDocument.Parse(store.BuildRestoreJson());
		var session = message.RootElement.GetProperty("session");

		Assert.Equal(JsonValueKind.Null, session.GetProperty("active").ValueKind);
		Assert.Empty(session.GetProperty("open").EnumerateArray());
	}

	[Fact]
	public void BuildRestoreJson_StampsSessionId() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText("/root/file.ts", "x");
		var session = new EditorSession { Active = "/root/file.ts", Open = [new EditorSessionEntry { Path = "/root/file.ts" }] };

		using var message = JsonDocument.Parse(
			EditorSessionStore.BuildRestoreJson(session, fs, "/root", "abc123", log: null));

		Assert.Equal("abc123", message.RootElement.GetProperty("sessionId").GetString());
	}

	[Fact]
	public void BuildRestoreJson_DropsTabOutsideWorkspaceRoot() {
		// A tab in another session's worktree (outside this root) exists on disk so the existence check passes,
		// but must not be restored: this session's file provider would refuse it out-of-root and open blank.
		// A scratch buffer (outside the root by design) is kept.
		var fs = new InMemoryFileSystem();
		fs.WriteAllText("/root/in.ts", "x");
		fs.WriteAllText("/elsewhere/worktree/foreign.ts", "y");
		fs.WriteAllText("/scratch/untitled-1", "z");
		var session = new EditorSession {
			Active = "/elsewhere/worktree/foreign.ts",
			Open = [
				new EditorSessionEntry { Path = "/root/in.ts" },
				new EditorSessionEntry { Path = "/elsewhere/worktree/foreign.ts" },
				new EditorSessionEntry { Path = "/scratch/untitled-1", Scratch = true },
			],
		};

		using var message = JsonDocument.Parse(
			EditorSessionStore.BuildRestoreJson(session, fs, "/root", sessionId: null, log: null));
		var open = message.RootElement.GetProperty("session").GetProperty("open").EnumerateArray()
			.Select(e => e.GetProperty("path").GetString()).ToList();

		Assert.Contains("/root/in.ts", open);
		Assert.Contains("/scratch/untitled-1", open);
		Assert.DoesNotContain("/elsewhere/worktree/foreign.ts", open);
		// Dropped file was active → active is nulled, not left pointing at a tab that won't open.
		Assert.Equal(JsonValueKind.Null, message.RootElement.GetProperty("session").GetProperty("active").ValueKind);
	}
}
