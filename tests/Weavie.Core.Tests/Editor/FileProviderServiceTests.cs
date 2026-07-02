using System.Text;
using System.Text.Json;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests.Editor;

/// <summary>
/// <see cref="FileProviderService.ReadBytes"/> answers <c>fs-read-bytes</c> with the file's raw bytes as
/// base64 under the same workspace confinement as the text read, and <see cref="FileProviderService.CanRead"/>
/// gates opens without reading content.
/// </summary>
public sealed class FileProviderServiceTests {
	private static readonly string Workspace = OperatingSystem.IsWindows() ? @"C:\ws" : "/ws";
	private static readonly string Scratch = OperatingSystem.IsWindows() ? @"C:\scratch" : "/scratch";

	private static (FileProviderService service, InMemoryFileSystem fs) New() {
		var fs = new InMemoryFileSystem();
		return (new FileProviderService(fs, Workspace, Scratch), fs);
	}

	[Fact]
	public void ReadBytes_ReturnsBase64OfTheFile() {
		var (service, fs) = New();
		string path = Path.Combine(Workspace, "pic.png");
		fs.WriteAllText(path, "raw-bytes");

		var root = JsonDocument.Parse(service.ReadBytes("m1", path)).RootElement;

		Assert.Equal("fs-read-bytes-result", root.GetProperty("type").GetString());
		Assert.Equal("m1", root.GetProperty("id").GetString());
		Assert.True(root.GetProperty("ok").GetBoolean());
		Assert.Equal("raw-bytes", Encoding.UTF8.GetString(root.GetProperty("dataB64").GetBytesFromBase64()));
		Assert.Equal(Encoding.UTF8.GetByteCount("raw-bytes"), root.GetProperty("size").GetInt64());
	}

	[Fact]
	public void ReadBytes_OutOfWorkspace_IsFileNotFound() {
		var (service, fs) = New();
		string outside = OperatingSystem.IsWindows() ? @"C:\other\pic.png" : "/other/pic.png";
		fs.WriteAllText(outside, "raw-bytes"); // exists on disk, but outside the worktree + scratch

		var root = JsonDocument.Parse(service.ReadBytes("m1", outside)).RootElement;

		Assert.False(root.GetProperty("ok").GetBoolean());
		Assert.Equal("FileNotFound", root.GetProperty("code").GetString());
	}

	[Fact]
	public void ReadBytes_ScratchRoot_IsAllowed() {
		var (service, fs) = New();
		string path = Path.Combine(Scratch, "clip.webm");
		fs.WriteAllText(path, "vid");

		Assert.True(JsonDocument.Parse(service.ReadBytes("m1", path)).RootElement.GetProperty("ok").GetBoolean());
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
