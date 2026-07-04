using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Pasted-image store: sequential <c>paste-N</c> allocation (skipping taken numbers), byte-exact writes, and
/// wholesale <see cref="PastedImageStore.Clear"/> on unload. Plus the MIME→extension allowlist.
/// </summary>
public sealed class PastedImageStoreTests {
	// High bytes (>127) would corrupt if stored as text — proves a real byte round-trip.
	private static readonly byte[] PngBytes = [0x89, 0x50, 0x4e, 0x47, 0xff, 0x00, 0xfe, 0x7f];

	private static string TempDir() => Path.Combine(Path.GetTempPath(), $"weavie-pasted-{Guid.NewGuid():N}");

	[Fact]
	public void Write_AllocatesSequentialFiles_WithExtensionAndExactBytes() {
		var fs = new InMemoryFileSystem();
		string dir = TempDir();
		var store = new PastedImageStore(fs, dir);

		string first = store.Write(".png", PngBytes);
		string second = store.Write(".png", PngBytes);

		Assert.Equal(Path.Combine(dir, "paste-1.png"), first);
		Assert.Equal(Path.Combine(dir, "paste-2.png"), second);
		Assert.Equal(PngBytes, fs.ReadAllBytes(first));
	}

	[Fact]
	public void Write_SkipsNumbersAlreadyOnDisk() {
		var fs = new InMemoryFileSystem();
		string dir = TempDir();
		var store = new PastedImageStore(fs, dir);
		fs.WriteAllBytes(Path.Combine(dir, "paste-1.png"), PngBytes);

		Assert.Equal(Path.Combine(dir, "paste-2.png"), store.Write(".png", PngBytes));
	}

	[Fact]
	public void Clear_RemovesEveryPastedImage() {
		var fs = new InMemoryFileSystem();
		string dir = TempDir();
		var store = new PastedImageStore(fs, dir);
		string a = store.Write(".png", PngBytes);
		string b = store.Write(".gif", PngBytes);

		store.Clear();

		Assert.False(fs.FileExists(a));
		Assert.False(fs.FileExists(b));
	}

	[Fact]
	public void Clear_OnAnEmptyDir_IsANoOp() {
		var fs = new InMemoryFileSystem();
		var store = new PastedImageStore(fs, TempDir());

		store.Clear(); // never written to; must not throw
	}

	[Theory]
	[InlineData("image/png", ".png")]
	[InlineData("image/jpeg", ".jpg")]
	[InlineData("image/gif", ".gif")]
	[InlineData("image/webp", ".webp")]
	public void TryExtension_MapsAllowedTypes(string mime, string extension) {
		Assert.True(PastedImageMedia.TryExtension(mime, out string ext));
		Assert.Equal(extension, ext);
	}

	[Theory]
	[InlineData("image/svg+xml")]
	[InlineData("image/bmp")]
	[InlineData("text/plain")]
	[InlineData("")]
	public void TryExtension_RejectsDisallowedTypes(string mime) {
		Assert.False(PastedImageMedia.TryExtension(mime, out string ext));
		Assert.Equal(string.Empty, ext);
	}
}
