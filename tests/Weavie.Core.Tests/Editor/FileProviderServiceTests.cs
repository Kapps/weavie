using System.Text.Json;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests.Editor;

/// <summary>
/// <see cref="FileProviderService"/> confines text reads and writes to the workspace/scratch roots, and
/// <see cref="FileProviderService.CanRead"/> gates opens without reading content.
/// </summary>
public sealed class FileProviderServiceTests {
	private static readonly string Workspace = OperatingSystem.IsWindows() ? @"C:\ws" : "/ws";
	private static readonly string Scratch = OperatingSystem.IsWindows() ? @"C:\scratch" : "/scratch";

	private static (FileProviderService service, InMemoryFileSystem fs) New() {
		var fs = new InMemoryFileSystem();
		return (new FileProviderService(fs, Workspace, Scratch), fs);
	}

	[Fact]
	public void Read_BinaryFile_ReturnsSmallTextError() {
		var (service, fs) = New();
		string path = Path.Combine(Workspace, "archive.bin");
		fs.WriteAllBytes(path, [0x50, 0x4b, 0x00, 0xff]);

		var root = JsonDocument.Parse(service.Read("r1", path)).RootElement;

		Assert.False(root.GetProperty("ok").GetBoolean());
		Assert.Equal("Binary files cannot be opened as text.", root.GetProperty("error").GetString());
		Assert.False(root.TryGetProperty("content", out _));
	}

	[Fact]
	public void CanRead_MatchesConfinementAndExistence() {
		var (service, fs) = New();
		string inside = Path.Combine(Workspace, "a.cs");
		string outside = OperatingSystem.IsWindows() ? @"C:\other\a.cs" : "/other/a.cs";
		fs.WriteAllText(inside, "x");
		fs.WriteAllText(outside, "x");

		Assert.True(service.CanRead(inside));
		Assert.False(service.CanRead(outside)); // exists, but out of workspace
		Assert.False(service.CanRead(Path.Combine(Workspace, "ghost.cs"))); // in workspace, but missing
	}
}
