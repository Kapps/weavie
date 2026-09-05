using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class FileTreeSnapshotTests : IDisposable {
	private readonly TempDirectory _root = new("file-tree-snapshot-tests");

	[Fact]
	public void Directory_snapshot_is_independent_and_replaces_stale_destination_files() {
		string source = _root.CreateDirectory("source");
		_root.WriteFile(Path.Combine("source", "nested", "value.txt"), "production");
		string destinationRoot = _root.CreateDirectory("preview");
		string destination = _root.CreateDirectory("preview", "tree");
		File.WriteAllText(Path.Combine(destination, "stale.txt"), "stale");

		FileTreeSnapshot.MirrorDirectory(source, destination, destinationRoot);
		File.WriteAllText(Path.Combine(destination, "nested", "value.txt"), "preview");

		Assert.Equal("production", File.ReadAllText(Path.Combine(source, "nested", "value.txt")));
		Assert.False(File.Exists(Path.Combine(destination, "stale.txt")));
	}

	[Fact]
	public void Source_links_are_rejected_without_touching_the_destination() {
		string target = _root.CreateDirectory("target");
		File.WriteAllText(Path.Combine(target, "value.txt"), "production");
		string source = _root.Combine("source-link");
		Directory.CreateSymbolicLink(source, target);
		string destinationRoot = _root.CreateDirectory("preview");
		string destination = _root.CreateDirectory("preview", "tree");
		File.WriteAllText(Path.Combine(destination, "existing.txt"), "preview");

		Assert.Throws<InvalidOperationException>(
			() => FileTreeSnapshot.MirrorDirectory(source, destination, destinationRoot));

		Assert.Equal("preview", File.ReadAllText(Path.Combine(destination, "existing.txt")));
	}

	[Fact]
	public void Destination_link_components_are_rejected_without_writing_through_them() {
		string source = _root.CreateDirectory("source");
		File.WriteAllText(Path.Combine(source, "value.txt"), "production");
		string outside = _root.CreateDirectory("outside");
		string destinationRoot = _root.CreateDirectory("preview");
		Directory.CreateSymbolicLink(Path.Combine(destinationRoot, "linked"), outside);

		Assert.Throws<InvalidOperationException>(() => FileTreeSnapshot.MirrorDirectory(
			source,
			Path.Combine(destinationRoot, "linked", "tree"),
			destinationRoot));

		Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
	}

	[Fact]
	public void File_snapshot_preserves_executable_mode_on_posix() {
		if (OperatingSystem.IsWindows()) {
			return;
		}
		string source = _root.WriteFile("tool", "tool");
		File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserExecute);
		string destinationRoot = _root.CreateDirectory("preview");
		string destination = Path.Combine(destinationRoot, "tool");

		FileTreeSnapshot.MirrorFile(source, destination, destinationRoot);

		Assert.Equal(File.GetUnixFileMode(source), File.GetUnixFileMode(destination));
	}

	public void Dispose() => _root.Dispose();
}
