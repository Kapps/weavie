using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Scratch (untitled-buffer) store: sequential "Untitled-N" allocation skipping taken numbers, scoped delete,
/// and GC of unreferenced buffers. Also pins the file provider's scratch root so untitled buffers (outside the
/// workspace) are read/writable while out-of-bounds paths stay refused.
/// </summary>
public sealed class ScratchStoreTests {
	private static string TempDir(string label) =>
		Path.Combine(Path.GetTempPath(), $"weavie-{label}-{Guid.NewGuid():N}");

	[Fact]
	public void CreateNew_AllocatesSequentialUntitledFiles() {
		var fs = new InMemoryFileSystem();
		string dir = TempDir("scratch");
		var store = new ScratchStore(fs, dir);

		string first = store.CreateNew();
		string second = store.CreateNew();

		Assert.Equal(Path.Combine(dir, "Untitled-1"), first);
		Assert.Equal(Path.Combine(dir, "Untitled-2"), second);
		Assert.True(fs.FileExists(first));
		Assert.Equal(string.Empty, fs.ReadAllText(first));
	}

	[Fact]
	public void CreateNew_SkipsNumbersAlreadyOnDisk() {
		var fs = new InMemoryFileSystem();
		string dir = TempDir("scratch");
		var store = new ScratchStore(fs, dir);
		fs.WriteAllText(Path.Combine(dir, "Untitled-1"), "taken");

		Assert.Equal(Path.Combine(dir, "Untitled-2"), store.CreateNew());
	}

	[Fact]
	public void Delete_RemovesOwnedFile_RefusesOutside() {
		var fs = new InMemoryFileSystem();
		string dir = TempDir("scratch");
		var store = new ScratchStore(fs, dir);
		string scratch = store.CreateNew();
		string outside = Path.Combine(TempDir("elsewhere"), "note.txt");
		fs.WriteAllText(outside, "keep me");

		Assert.True(store.Delete(scratch));
		Assert.False(fs.FileExists(scratch));

		Assert.False(store.Delete(outside));
		Assert.True(fs.FileExists(outside));
	}

	[Fact]
	public void Owns_UsesThePlatformFileSystemCaseRules() {
		var fs = new InMemoryFileSystem();
		string parent = TempDir("scratch-case");
		string directory = Path.Combine(parent, "Scratch");
		var store = new ScratchStore(fs, directory);
		string caseVariant = Path.Combine(parent, "scratch", "Untitled-1");

		Assert.Equal(OperatingSystem.IsWindows(), store.Owns(caseVariant));
	}

	[Fact]
	public void Inspect_ReportsEveryNonEmptyReferencedScratchIncludingWhitespace() {
		var fs = new InMemoryFileSystem();
		string dir = TempDir("scratch");
		var store = new ScratchStore(fs, dir);
		string empty = store.CreateNew();
		string whitespace = store.CreateNew();
		string text = store.CreateNew();
		fs.WriteAllText(whitespace, " \n");
		fs.WriteAllText(text, "keep this draft");

		var snapshots = store.Inspect(new EditorSession {
			Open = [
				new EditorSessionEntry { Path = empty, Scratch = true },
				new EditorSessionEntry { Path = whitespace, Scratch = true },
				new EditorSessionEntry { Path = text, Scratch = true },
				new EditorSessionEntry { Path = Path.Combine(dir, "ordinary.txt") },
			],
		});

		Assert.Equal([whitespace, text], snapshots.Select(snapshot => snapshot.Path));
		Assert.All(snapshots, snapshot => Assert.NotEmpty(snapshot.ContentHash));
	}

	[Fact]
	public void Inspect_RejectsMissingAndOutsideScratchEntries() {
		var fs = new InMemoryFileSystem();
		string dir = TempDir("scratch");
		var store = new ScratchStore(fs, dir);
		string missing = Path.Combine(dir, "Untitled-1");
		string outside = Path.Combine(TempDir("outside"), "Untitled-2");

		Assert.Throws<FileNotFoundException>(() => store.Inspect(new EditorSession {
			Open = [new EditorSessionEntry { Path = missing, Scratch = true }],
		}));
		Assert.Throws<InvalidDataException>(() => store.Inspect(new EditorSession {
			Open = [new EditorSessionEntry { Path = outside, Scratch = true }],
		}));
	}

	[Fact]
	public void DeleteReferenced_RemovesOnlyOwnedScratchEntries() {
		var fs = new InMemoryFileSystem();
		string dir = TempDir("scratch");
		var store = new ScratchStore(fs, dir);
		string first = store.CreateNew();
		string second = store.CreateNew();

		store.DeleteReferenced(new EditorSession {
			Open = [new EditorSessionEntry { Path = first, Scratch = true }],
		});

		Assert.False(fs.FileExists(first));
		Assert.True(fs.FileExists(second));
	}

	[Fact]
	public void GarbageCollect_DeletesUnreferenced_KeepsReferenced() {
		var fs = new InMemoryFileSystem();
		string dir = TempDir("scratch");
		var store = new ScratchStore(fs, dir);
		string keep = store.CreateNew();
		string drop1 = store.CreateNew();
		string drop2 = store.CreateNew();

		int removed = store.GarbageCollect([keep]);

		Assert.Equal(2, removed);
		Assert.True(fs.FileExists(keep));
		Assert.False(fs.FileExists(drop1));
		Assert.False(fs.FileExists(drop2));
	}
}
