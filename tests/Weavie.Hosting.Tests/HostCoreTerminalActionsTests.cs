using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// End-to-end tests for the terminal's host-OS actions (clipboard + open-url), driving the same web messages
/// the page sends and asserting the host routes them to the platform — and replies to a clipboard read.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreTerminalActionsTests {
	[Fact]
	public async Task ClipboardWrite_WritesTheTextToThePlatform() {
		await using var host = await TestHost.StartAsync();

		host.HostEvent("clipboard", "write", new { text = "copied from the terminal" });

		Assert.Equal("copied from the terminal", host.Platform.LastWrittenClipboard);
	}

	[Fact]
	public async Task ClipboardRead_RepliesWithTheClipboardContentTaggedById() {
		await using var host = await TestHost.StartAsync();
		host.Platform.ClipboardValue = "paste me";

		var reply = await host.HostRequestAsync<System.Text.Json.JsonElement>(
			"clipboard",
			"read",
			new { });

		Assert.Equal("paste me", reply.GetProperty("text").GetString());
	}

	[Theory]
	[InlineData("https://example.com/auth?code=abc")]
	[InlineData("http://localhost:8080/callback")]
	public async Task OpenUrl_OpensHttpUrlsViaThePlatform(string url) {
		await using var host = await TestHost.StartAsync();

		host.HostEvent("platform", "openUrl", new { url });

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

		host.HostEvent("platform", "openUrl", new { url });

		Assert.Null(host.Platform.LastOpenedUrl); // the OS opener was never reached
	}

	[Fact]
	public async Task MalformedMessage_IsContainedAndTheHostKeepsWorking() {
		await using var host = await TestHost.StartAsync();

		// Bad base64 in term-input throws inside the dispatch; the backstop must contain it (it would otherwise
		// crash the network-exposed worker), and the host keeps handling subsequent messages.
		host.SessionEvent(
			host.PrimarySession,
			"terminal.shell",
			"input",
			new { dataB64 = "!!! not base64 !!!" });
		host.HostEvent("clipboard", "write", new { text = "still working" });

		Assert.Equal("still working", host.Platform.LastWrittenClipboard);
	}
}
