using System.Text.Json;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// End-to-end tests for the in-app log viewer (<c>weavie.view.logs</c>): the command must post
/// <c>source-loading</c> FIRST — the only message the web opens a source tab on — then a <c>source-doc</c>
/// carrying the buffer as pre-rendered <c>html</c> (and an <c>editedTime</c>, per the source-doc contract).
/// </summary>
[Collection("host-integration")]
public sealed class HostCoreLogsTests {
	[Fact]
	public async Task ViewLogs_OpensTheTabThenFillsItWithEscapedHtml() {
		await using var host = await TestHost.StartAsync();
		host.LogBuffer.Append("boot ok <tag> & done");

		host.Send("""{"type":"invoke-command","id":"weavie.view.logs"}""");

		var doc = await Wait.ForAsync(() => host.Bridge.LastOfType("source-doc"));
		Assert.Equal("about:logs", doc.GetProperty("target").GetString());
		Assert.Equal("Weavie Logs", doc.GetProperty("title").GetString());
		Assert.Equal("", doc.GetProperty("editedTime").GetString());
		string html = doc.GetProperty("html").GetString()!;
		Assert.Contains("boot ok &lt;tag&gt; &amp; done", html); // log text is HTML-encoded inside the <pre>
		Assert.StartsWith("<pre>", html); // nothing dropped → no marker ahead of the log body

		// The tab-opening message precedes the doc — the web opens source tabs only on source-loading.
		var loading = host.Bridge.LastOfType("source-loading");
		Assert.Equal("about:logs", loading!.Value.GetProperty("target").GetString());
		int loadingIndex = IndexOfType(host.Bridge.Posted, "source-loading");
		int docIndex = IndexOfType(host.Bridge.Posted, "source-doc");
		Assert.True(loadingIndex < docIndex, "source-loading must be posted before source-doc");
	}

	private static int IndexOfType(IReadOnlyList<string> posted, string type) {
		for (int i = 0; i < posted.Count; i++) {
			using var parsed = JsonDocument.Parse(posted[i]);
			if (parsed.RootElement.TryGetProperty("type", out var t) && t.GetString() == type) {
				return i;
			}
		}

		return -1;
	}
}
