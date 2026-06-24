using System.Text.Json;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// End-to-end tests for the terminal's host-OS actions (clipboard + open-url), driving the same web messages
/// the page sends and asserting the host routes them to the platform — and replies to a clipboard read.
/// </summary>
[Collection("host-integration")]
public sealed class HostCoreTerminalActionsTests {
	private static string Msg(object value) => JsonSerializer.Serialize(value);

	[Fact]
	public async Task ClipboardWrite_WritesTheTextToThePlatform() {
		await using var host = await TestHost.StartAsync();

		host.Send(Msg(new { type = "clipboard-write", text = "copied from the terminal" }));

		Assert.Equal("copied from the terminal", host.Platform.LastWrittenClipboard);
	}

	[Fact]
	public async Task ClipboardRead_RepliesWithTheClipboardContentTaggedById() {
		await using var host = await TestHost.StartAsync();
		host.Platform.ClipboardValue = "paste me";

		host.Send(Msg(new { type = "clipboard-read", id = "c1" }));

		var reply = host.Bridge.LastOfType("clipboard-content");
		Assert.True(reply.HasValue);
		Assert.Equal("c1", reply!.Value.GetProperty("id").GetString());
		Assert.Equal("paste me", reply.Value.GetProperty("text").GetString());
	}

	[Theory]
	[InlineData("https://example.com/auth?code=abc")]
	[InlineData("http://localhost:8080/callback")]
	public async Task OpenUrl_OpensHttpUrlsViaThePlatform(string url) {
		await using var host = await TestHost.StartAsync();

		host.Send(Msg(new { type = "open-url", url }));

		Assert.Equal(url, host.Platform.LastOpenedUrl);
	}

	[Theory]
	[InlineData("file:///C:/Windows/System32/calc.exe")]
	[InlineData("file://attacker/share/evil.exe")]
	[InlineData("ms-msdt:/id PCWDiagnostic")]
	[InlineData("javascript:alert(1)")]
	[InlineData("C:\\Windows\\System32\\calc.exe")]
	[InlineData("not a url")]
	public async Task OpenUrl_RefusesNonHttpSchemes(string url) {
		await using var host = await TestHost.StartAsync();

		host.Send(Msg(new { type = "open-url", url }));

		Assert.Null(host.Platform.LastOpenedUrl); // the OS opener was never reached
	}

	[Fact]
	public async Task MalformedMessage_IsContainedAndTheHostKeepsWorking() {
		await using var host = await TestHost.StartAsync();

		// Bad base64 in term-input throws inside the dispatch; the backstop must contain it (it would otherwise
		// crash the network-exposed worker), and the host keeps handling subsequent messages.
		host.Send("""{"type":"term-input","dataB64":"!!! not base64 !!!"}""");
		host.Send(Msg(new { type = "clipboard-write", text = "still working" }));

		Assert.Equal("still working", host.Platform.LastWrittenClipboard);
	}
}
