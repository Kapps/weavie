using Weavie.Core.Editor;
using Xunit;

namespace Weavie.Core.Tests.Editor;

public sealed class WorkspaceFileScopeTests : IDisposable {
	private readonly string _root = Path.Combine(Path.GetTempPath(), "weavie-file-scope", Guid.NewGuid().ToString("N"));

	public WorkspaceFileScopeTests() {
		Directory.CreateDirectory(_root);
	}

	[Fact]
	public void ResolvePhysicalPath_RejectsAFileSymlinkOutsideTheWorkspace() {
		string workspace = Path.Combine(_root, "workspace");
		string outside = Path.Combine(_root, "outside.txt");
		Directory.CreateDirectory(workspace);
		File.WriteAllText(outside, "secret");
		string link = Path.Combine(workspace, "outside.txt");
		File.CreateSymbolicLink(link, outside);

		var scope = new WorkspaceFileScope([workspace]);

		Assert.Throws<UnauthorizedAccessException>(() => scope.ResolvePhysicalPath(link, allowMissingLeaf: false));
	}

	[Fact]
	public void ResolvePhysicalPath_RejectsANewFileThroughAnOutsideDirectorySymlink() {
		string workspace = Path.Combine(_root, "workspace");
		string outside = Path.Combine(_root, "outside");
		Directory.CreateDirectory(workspace);
		Directory.CreateDirectory(outside);
		string link = Path.Combine(workspace, "outside");
		Directory.CreateSymbolicLink(link, outside);

		var scope = new WorkspaceFileScope([workspace]);

		Assert.Throws<UnauthorizedAccessException>(() =>
			scope.ResolvePhysicalPath(Path.Combine(link, "new.txt"), allowMissingLeaf: true));
	}

	[Fact]
	public void ResolvePhysicalPath_AllowsANewLeafUnderAnExistingWorkspaceDirectory() {
		string workspace = Path.Combine(_root, "workspace");
		string directory = Path.Combine(workspace, "nested");
		Directory.CreateDirectory(directory);
		var scope = new WorkspaceFileScope([workspace]);

		string result = scope.ResolvePhysicalPath(Path.Combine(directory, "new.txt"), allowMissingLeaf: true);

		Assert.Equal(Path.Combine(directory, "new.txt"), result);
	}

	[Fact]
	public void ResolvePhysicalPath_UsesCaseSensitiveContainmentOnLinux() {
		if (!OperatingSystem.IsLinux()) return;
		string workspace = Path.Combine(_root, "workspace");
		string differentlyCased = Path.Combine(_root, "WORKSPACE");
		Directory.CreateDirectory(workspace);
		Directory.CreateDirectory(differentlyCased);
		string path = Path.Combine(differentlyCased, "file.txt");
		File.WriteAllText(path, "outside");
		var scope = new WorkspaceFileScope([workspace]);

		Assert.Throws<UnauthorizedAccessException>(() => scope.ResolvePhysicalPath(path, allowMissingLeaf: false));
	}

	public void Dispose() {
		if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
	}
}
