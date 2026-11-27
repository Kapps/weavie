using Weavie.Core.Agents;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class WalkthroughCaptureTests : IDisposable {
	private readonly string _root = Directory.CreateTempSubdirectory("weavie-walkthrough-").FullName;

	public void Dispose() => Directory.Delete(_root, recursive: true);

	private string? Error(string path, Func<string, bool> late) =>
		new WalkthroughCapture(new LocalFileSystem(), _root, late).TryRead(path, out _);

	[Fact]
	public void RefusesWhatItCantCite() {
		File.WriteAllBytes(Path.Combine(_root, "image.png"), [0x89, 0x50, 0x00, 0x00, 0xff]);
		File.WriteAllText(Path.Combine(_root, "big.txt"), new string('x', AgentWalkthroughLimits.FileBytes + 1));

		Assert.Equal($"'../outside.cs' isn't a file inside {_root}.", Error("../outside.cs", _ => false));
		Assert.Equal("'missing.cs' doesn't exist.", Error("missing.cs", _ => false));
		Assert.Equal("'image.png' isn't a text file.", Error("image.png", _ => false));
		Assert.Equal("'big.txt' is 2,097,153 bytes; a cited file is at most 2,097,152.", Error("big.txt", _ => false));
	}

	[Fact]
	public void AFileWithUnkeptAgentEditsIsLate() {
		File.WriteAllText(Path.Combine(_root, "a.cs"), "class A {}\n");
		string? asked = null;

		var reads = new WalkthroughCapture(new LocalFileSystem(), _root, path => (asked = path) is not null).Begin();
		var cited = reads.Cite("a.cs", "at");

		Assert.True(cited.Late);
		Assert.Equal(Path.Combine(_root, "a.cs"), asked);
		Assert.Equal(("a.cs", WorktreeTextFile.HashOf("class A {}\n")), (cited.File.Path, cited.File.Hash));
		Assert.Same(cited, reads.Cite("a.cs", "again"));
	}
}
