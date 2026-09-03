using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests.Editor;

/// <summary>
/// <see cref="FileProviderService"/> shapes text reads and writes for the bridge, and
/// <see cref="FileProviderService.CanRead"/> gates opens on existence without reading content.
/// </summary>
public sealed class FileProviderServiceTests {
	private static readonly string Workspace = OperatingSystem.IsWindows() ? @"C:\ws" : "/ws";
	private static readonly string Scratch = OperatingSystem.IsWindows() ? @"C:\scratch" : "/scratch";

	private static (FileProviderService service, InMemoryFileSystem fs) New() {
		var fs = new InMemoryFileSystem();
		return (new FileProviderService(fs), fs);
	}

	[Fact]
	public void Read_BinaryFile_ReturnsSmallTextError() {
		var (service, fs) = New();
		string path = Path.Combine(Workspace, "archive.bin");
		fs.WriteAllBytes(path, [0x50, 0x4b, 0x00, 0xff]);

		var result = service.Read(path);

		Assert.False(result.Ok);
		Assert.Equal("Binary files cannot be opened as text.", result.Error);
		Assert.Null(result.Content);
	}

	[Fact]
	public void CanRead_TracksExistenceWhereverTheFileLives() {
		var (service, fs) = New();
		string inside = Path.Combine(Workspace, "a.cs");
		string outside = OperatingSystem.IsWindows() ? @"C:\other\a.cs" : "/other/a.cs";
		fs.WriteAllText(inside, "x");
		fs.WriteAllText(outside, "x");

		Assert.True(service.CanRead(inside));
		Assert.True(service.CanRead(outside)); // outside the worktree, but the user can still open it
		Assert.False(service.CanRead(Path.Combine(Workspace, "ghost.cs")));
	}

	[Fact]
	public void ReadAndWrite_ServeAPathOutsideTheWorktree() {
		var (service, fs) = New();
		string outside = OperatingSystem.IsWindows() ? @"C:\other\notes.md" : "/other/notes.md";
		fs.WriteAllText(outside, "before");

		Assert.Equal("before", service.Read(outside).Content);
		Assert.True(service.Write(outside, "after").Ok);
		Assert.Equal("after", service.ReadText(outside));
	}
}
