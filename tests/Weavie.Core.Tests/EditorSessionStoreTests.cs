using System.Text.Json;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="EditorSessionStore"/> over the in-memory filesystem: empty-on-missing, persist +
/// reload (view state opaque round-trip), malformed-file backup + reset, and the restore push reading
/// content from disk while skipping (and de-activating) files that no longer exist.
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
	public void BuildRestoreJson_AddsDiskContentForExistingFiles() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(FilePath, "export const x = 1;\n");
		var store = NewStore(fs);
		store.Update(new EditorSession {
			Active = FilePath,
			Open = [new EditorSessionEntry { Path = FilePath }],
		});

		using var message = JsonDocument.Parse(store.BuildRestoreJson());
		var session = message.RootElement.GetProperty("session");

		Assert.Equal("set-editor-session", message.RootElement.GetProperty("type").GetString());
		Assert.Equal(FilePath, session.GetProperty("active").GetString());
		var entry = session.GetProperty("open").EnumerateArray().Single();
		Assert.Equal("export const x = 1;\n", entry.GetProperty("content").GetString());
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
}
