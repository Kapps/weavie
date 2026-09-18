using System.Text.Json;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// End-to-end tests for the in-app log viewer (<c>weavie.view.logs</c>): the command must fill the
/// <c>about:logs</c> document with the buffer as pre-rendered <c>html</c> AND open the source overlay — the host
/// owns tab opening, so a document without an <c>openOverlay</c> is a toast with nothing behind it.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreLogsTests {
	[Fact]
	public async Task ViewLogs_OpensTheTabAndFillsItWithEscapedHtml() {
		await using var host = await TestHost.StartAsync();
		host.LogBuffer.Append("boot ok <tag> & done");

		var result = await host.InvokeClientCommandAsync("weavie.view.logs", new { });
		Assert.True(result.Ok, result.Error);

		var doc = await Wait.ForAsync(() =>
			host.Bridge.LastEvent(host.SelectedSession.Address, "sources", "document"));
		Assert.Equal("about:logs", doc.GetProperty("target").GetString());
		Assert.Equal("Weavie Logs", doc.GetProperty("title").GetString());
		Assert.Equal("", doc.GetProperty("editedTime").GetString());
		Assert.Equal("logs", doc.GetProperty("sourceId").GetString()); // keys the tab icon web-side
		string html = doc.GetProperty("html").GetString()!;
		Assert.Contains("boot ok &lt;tag&gt; &amp; done", html); // log text is HTML-encoded inside the <pre>
		Assert.StartsWith("<pre>", html); // nothing dropped → no marker ahead of the log body

		// The tab itself: nothing opens without this, which is exactly what a "viewing N of Y logs" toast hides.
		var overlay = await Wait.ForAsync(() =>
			host.Bridge.LastEvent(host.SelectedSession.Address, "editor", "openOverlay"));
		Assert.Equal("about:logs", overlay.GetProperty("path").GetString());
		Assert.Equal("source", overlay.GetProperty("kind").GetString());
		// Content before the tab, so the SourceView never paints an empty overlay.
		Assert.True(
			IndexOfEvent(host.Bridge.Posted, "sources", "document")
			< IndexOfEvent(host.Bridge.Posted, "editor", "openOverlay"),
			"the logs document must be posted before the tab that renders it");
	}

	[Fact]
	public async Task ViewLogs_ReplaysTheDocumentToAReconnectingClient() {
		await using var host = await TestHost.StartAsync();
		host.LogBuffer.Append("boot ok");

		var result = await host.InvokeClientCommandAsync("weavie.view.logs", new { });
		Assert.True(result.Ok, result.Error);
		await Wait.ForAsync(() =>
			host.Bridge.LastEvent(host.SelectedSession.Address, "sources", "document"));

		host.Bridge.Clear();

		await host.SessionRequestAsync<JsonElement>(
			host.SelectedSession,
			"lifecycle",
			"sync",
			new { });

		// The tab survives a reload (it's in the editor session), so its content must come back with it.
		var replayed = host.Bridge.LastEvent(host.SelectedSession.Address, "sources", "document");
		Assert.True(replayed.HasValue);
		Assert.Equal("about:logs", replayed!.Value.GetProperty("target").GetString());
		Assert.Contains("boot ok", replayed.Value.GetProperty("html").GetString()!);
		var restore = host.Bridge.LastEvent(host.SelectedSession.Address, "editor", "restore");
		Assert.Contains(
			restore!.Value.GetProperty("session").GetProperty("open").EnumerateArray(),
			entry => entry.GetProperty("path").GetString() == "about:logs"
				&& entry.GetProperty("kind").GetString() == "source");
	}

	private static int IndexOfEvent(
		IReadOnlyList<string> posted,
		string feature,
		string name) {
		for (int i = 0; i < posted.Count; i++) {
			if (Messaging.MessageEnvelope.TryParse(posted[i], out var envelope)
				&& envelope is { Kind: Messaging.MessageKind.Event }
				&& envelope.Feature == feature
				&& envelope.Name == name) {
				return i;
			}
		}

		return -1;
	}
}
