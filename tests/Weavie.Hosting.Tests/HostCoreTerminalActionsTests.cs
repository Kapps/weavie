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

	[Fact]
	public async Task OpenUrl_OpensTheUrlViaThePlatform() {
		await using var host = await TestHost.StartAsync();

		host.Send(Msg(new { type = "open-url", url = "https://example.com/auth?code=abc" }));

		Assert.Equal("https://example.com/auth?code=abc", host.Platform.LastOpenedUrl);
	}
}
