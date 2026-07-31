using System.Text.Json;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// End-to-end tests for the in-app log viewer (<c>weavie.view.logs</c>): the command must post
/// <c>source-loading</c> FIRST — the only message the web opens a source tab on — then a <c>source-doc</c>
/// carrying the buffer as pre-rendered <c>html</c> (and an <c>editedTime</c>, per the source-doc contract).
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreLogsTests {
	[Fact]
	public async Task ViewLogs_OpensTheTabThenFillsItWithEscapedHtml() {
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

		// The tab-opening message precedes the doc — the web opens source tabs only on source-loading.
		var loading = host.Bridge.LastEvent(host.SelectedSession.Address, "sources", "loading");
		Assert.Equal("about:logs", loading!.Value.GetProperty("target").GetString());
		int loadingIndex = IndexOfEvent(host.Bridge.Posted, "sources", "loading");
		int docIndex = IndexOfEvent(host.Bridge.Posted, "sources", "document");
		Assert.True(loadingIndex < docIndex, "source-loading must be posted before source-doc");
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
