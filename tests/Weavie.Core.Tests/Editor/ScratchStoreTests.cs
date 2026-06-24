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

	[Fact]
	public void FileProvider_AllowsScratchRoot_RefusesOutOfBounds() {
		var fs = new InMemoryFileSystem();
		string workspace = TempDir("ws");
		string scratch = TempDir("scratch");
		var provider = new FileProviderService(fs, workspace, scratch);
		string scratchFile = Path.Combine(scratch, "Untitled-1");
		string workspaceFile = Path.Combine(workspace, "file.txt");
		string outsideFile = Path.Combine(TempDir("elsewhere"), "evil.txt");

		provider.Write("a", scratchFile, "scratch content");
		provider.Write("b", workspaceFile, "workspace content");
		provider.Write("c", outsideFile, "out of bounds");

		Assert.True(fs.FileExists(scratchFile));
		Assert.True(fs.FileExists(workspaceFile));
		Assert.False(fs.FileExists(outsideFile)); // outside both roots
	}

	[Fact]
	public void FileProvider_Read_RefusesOutOfBoundsEvenWhenFileExists() {
		var fs = new InMemoryFileSystem();
		string workspace = TempDir("ws");
		string scratch = TempDir("scratch");
		var provider = new FileProviderService(fs, workspace, scratch);
		string outsideFile = Path.Combine(TempDir("elsewhere"), "secret.txt");
		fs.WriteAllText(outsideFile, "secret content");

		string reply = provider.Read("r", outsideFile);

		// An on-disk file outside both roots must not be read — it answers FileNotFound, never its content.
		Assert.Contains("FileNotFound", reply);
		Assert.DoesNotContain("secret content", reply);
	}
}
