using System.Text.Json;
using Weavie.Core.Editor;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// End-to-end tests for pasting an image into the claude pane: the host writes the bytes to a per-session scratch
/// file and injects its path into the claude PTY as a bracketed paste (which the TUI attaches as an [Image #N]).
/// A disallowed type or oversize paste is surfaced as a toast and never written. See docs/specs/remote-paste-image.md.
/// </summary>
[Collection("host-integration")]
public sealed class HostCorePasteImageTests {
	private static readonly byte[] PngBytes = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3, 4, 5];
	private const string PasteBegin = "\x1b[200~";
	private const string PasteEnd = "\x1b[201~";

	private static string PasteImage(TestHost host, string mime, byte[] bytes) => JsonSerializer.Serialize(new {
		type = "term-paste-image",
		slot = host.PrimaryId,
		session = "claude",
		mime,
		dataB64 = Convert.ToBase64String(bytes),
	});

	private static string InjectedPath(NoopTerminal terminal) {
		string injected = terminal.WrittenText;
		Assert.StartsWith(PasteBegin, injected);
		Assert.EndsWith(PasteEnd, injected);
		return injected[PasteBegin.Length..^PasteEnd.Length];
	}

	private static NoopTerminal StartClaude(TestHost host) {
		host.Core.ActiveSessionForTest()!.Claude.EnsureStarted();
		return Assert.Single(host.Platform.NoopLauncher.Created);
	}

	[Fact]
	public async Task PasteImage_WritesAScratchFile_AndInjectsItsPathAsABracketedPaste() {
		await using var host = await TestHost.StartAsync();
		var claudeTerminal = StartClaude(host);

		host.Send(PasteImage(host, "image/png", PngBytes));

		string path = InjectedPath(claudeTerminal);
		Assert.EndsWith(".png", path);
		Assert.True(File.Exists(path), $"expected the pasted image on disk at {path}");
		Assert.Equal(PngBytes, File.ReadAllBytes(path));
	}

	[Theory]
	[InlineData("image/jpeg", ".jpg")]
	[InlineData("image/gif", ".gif")]
	[InlineData("image/webp", ".webp")]
	public async Task PasteImage_MapsEachAllowedMimeToItsExtension(string mime, string extension) {
		await using var host = await TestHost.StartAsync();
		var claudeTerminal = StartClaude(host);

		host.Send(PasteImage(host, mime, PngBytes));

		Assert.EndsWith(extension, InjectedPath(claudeTerminal));
	}

	[Theory]
	[InlineData("image/svg+xml")]
	[InlineData("text/plain")]
	public async Task PasteImage_RejectsADisallowedType_WithAToast_AndNoWrite(string mime) {
		await using var host = await TestHost.StartAsync();
		var claudeTerminal = StartClaude(host);

		host.Send(PasteImage(host, mime, PngBytes));

		Assert.Equal(0, claudeTerminal.WriteCount);
		var toast = host.Bridge.LastOfType("notify");
		Assert.True(toast.HasValue);
		Assert.Equal("warn", toast!.Value.GetProperty("level").GetString());
	}

	[Fact]
	public async Task PasteImage_RejectsAnOversizeImage_WithAToast_AndNoWrite() {
		await using var host = await TestHost.StartAsync();
		var claudeTerminal = StartClaude(host);

		host.Send(PasteImage(host, "image/png", new byte[PastedImageMedia.MaxBytes + 1]));

		Assert.Equal(0, claudeTerminal.WriteCount);
		Assert.True(host.Bridge.LastOfType("notify").HasValue);
	}

	[Fact]
	public async Task PasteImage_NamingTheShellPane_NeverWritesToTheShell() {
		await using var host = await TestHost.StartAsync();
		host.Core.ActiveSessionForTest()!.Shell.EnsureStarted();
		var shellTerminal = Assert.Single(host.Platform.NoopLauncher.Created);

		host.Send(JsonSerializer.Serialize(new {
			type = "term-paste-image",
			slot = host.PrimaryId,
			session = "shell",
			mime = "image/png",
			dataB64 = Convert.ToBase64String(PngBytes),
		}));

		Assert.Equal(0, shellTerminal.WriteCount); // pasted images only ever reach claude
	}

	[Fact]
	public async Task PasteImage_IsSuppressedWhileInputIsFrozenForAnUpdate() {
		await using var host = await TestHost.StartAsync();
		var claudeTerminal = StartClaude(host);

		host.Core.BeginDrain(() => { }); // a quiet host commits immediately → terminal input frozen
		host.Send(PasteImage(host, "image/png", PngBytes));

		Assert.Equal(0, claudeTerminal.WriteCount);
	}

	// The native-WebView half: on the claude pane the paste command reads the OS clipboard IMAGE through the local
	// host (clipboard-read-image), since the DOM paste event never fires there. The host replies with the bytes.
	[Fact]
	public async Task ClipboardReadImage_RepliesWithThePlatformsClipboardImage() {
		await using var host = await TestHost.StartAsync();
		host.Platform.ClipboardImageValue = new ClipboardImage("image/png", PngBytes);

		host.Send(JsonSerializer.Serialize(new { type = "clipboard-read-image", id = "img-1" }));

		var reply = host.Bridge.LastOfType("clipboard-image-content");
		Assert.True(reply.HasValue);
		Assert.Equal("img-1", reply!.Value.GetProperty("id").GetString());
		Assert.Equal("image/png", reply.Value.GetProperty("mime").GetString());
		Assert.Equal(PngBytes, Convert.FromBase64String(reply.Value.GetProperty("dataB64").GetString()!));
	}

	[Fact]
	public async Task ClipboardReadImage_WithNoImage_RepliesWithAnEmptyMime() {
		await using var host = await TestHost.StartAsync();

		host.Send(JsonSerializer.Serialize(new { type = "clipboard-read-image", id = "img-2" }));

		var reply = host.Bridge.LastOfType("clipboard-image-content");
		Assert.True(reply.HasValue);
		Assert.Equal(string.Empty, reply!.Value.GetProperty("mime").GetString());
	}
}
