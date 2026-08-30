using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class WorkspaceMediaServerTests {
	private static readonly HttpClient Http = new();

	[Fact]
	public async Task StreamsFullHeadAndRangeResponsesWithoutTheBridge() {
		await using var host = await TestHost.StartAsync();
		string path = Path.Combine(host.RepoRoot, "clip.webm");
		await File.WriteAllBytesAsync(path, Encoding.ASCII.GetBytes("0123456789"));
		string url = MediaUrl(host, host.WorkspaceIncarnation, path);

		using var full = await Http.GetAsync(url);
		Assert.Equal(HttpStatusCode.OK, full.StatusCode);
		Assert.Equal("0123456789", await full.Content.ReadAsStringAsync());
		Assert.Contains("bytes", full.Headers.AcceptRanges);
		Assert.NotNull(full.Headers.ETag);

		using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, url);
		rangeRequest.Headers.Range = new RangeHeaderValue(2, 5);
		using var range = await Http.SendAsync(rangeRequest);
		Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
		Assert.Equal("2345", await range.Content.ReadAsStringAsync());
		Assert.Equal(new ContentRangeHeaderValue(2, 5, 10), range.Content.Headers.ContentRange);

		using var invalidRangeRequest = new HttpRequestMessage(HttpMethod.Get, url);
		invalidRangeRequest.Headers.Range = new RangeHeaderValue(20, null);
		using var invalidRange = await Http.SendAsync(invalidRangeRequest);
		Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, invalidRange.StatusCode);

		using var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, url);
		conditionalRequest.Headers.IfNoneMatch.Add(full.Headers.ETag!);
		using var conditional = await Http.SendAsync(conditionalRequest);
		Assert.Equal(HttpStatusCode.NotModified, conditional.StatusCode);

		using var head = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url));
		Assert.Equal(HttpStatusCode.OK, head.StatusCode);
		Assert.Equal(10, head.Content.Headers.ContentLength);
	}

	[Fact]
	public async Task RequiresTheServerTokenAndServesOnlyPassiveMediaForALoadedSession() {
		await using var host = await TestHost.StartAsync();
		string inside = Path.Combine(host.RepoRoot, "pixel.png");
		string html = Path.Combine(host.RepoRoot, "page.html");
		string svg = Path.Combine(host.RepoRoot, "active.svg");
		await File.WriteAllBytesAsync(inside, [1, 2, 3]);
		await File.WriteAllTextAsync(html, "<script>window.top.pwned = true</script>");
		await File.WriteAllTextAsync(svg, "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
		string noToken = $"{host.Core.WorkspaceOrigin}/weavie-media/pixel.png?session={host.WorkspaceIncarnation}&path={Uri.EscapeDataString(inside)}";

		using var unauthorized = await Http.GetAsync(noToken);
		Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

		// Media outside the worktree streams like any other file — the editor can open it, so the pane shows it.
		string outside = Path.Combine(Path.GetDirectoryName(host.RepoRoot)!, "outside.png");
		await File.WriteAllBytesAsync(outside, [4, 5, 6]);
		using var direct = await Http.GetAsync(MediaUrl(host, host.WorkspaceIncarnation, outside));
		using var wrongSession = await Http.GetAsync(MediaUrl(host, "not-loaded", inside));
		using var wrongFileName = await Http.GetAsync(
			MediaUrl(host, host.WorkspaceIncarnation, inside).Replace("/pixel.png?", "/other.png?", StringComparison.Ordinal));
		using var activeHtml = await Http.GetAsync(MediaUrl(host, host.WorkspaceIncarnation, html));
		using var activeSvg = await Http.GetAsync(MediaUrl(host, host.WorkspaceIncarnation, svg));
		string malformed = $"{host.Core.WorkspaceOrigin}/weavie-media/malformed.png?token={host.Core.WorkspaceAccessToken}"
			+ $"&session={Uri.EscapeDataString(host.WorkspaceIncarnation)}&path=%00";
		using var malformedPath = await Http.GetAsync(malformed);

		Assert.Equal(HttpStatusCode.OK, direct.StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, wrongSession.StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, wrongFileName.StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, activeHtml.StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, activeSvg.StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, malformedPath.StatusCode);
		// Not a path rule: a file carrying no passive-media content type is never streamed, wherever it lives.
		if (File.Exists("/etc/passwd")) {
			using var systemFile = await Http.GetAsync(MediaUrl(host, host.WorkspaceIncarnation, "/etc/passwd"));
			Assert.Equal(HttpStatusCode.NotFound, systemFile.StatusCode);
		}
	}

	[Fact]
	public async Task UnregistersASecondarySessionsRouteBeforeItsBackendIsDisposed() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("media-route")).Ok);
		var session = host.SelectedSession;
		string path = Path.Combine(session.WorkspaceRoot, "session.png");
		await File.WriteAllBytesAsync(path, [11, 12]);
		string url = MediaUrl(host, session.Incarnation, path);

		using var loaded = await Http.GetAsync(url);
		Assert.Equal(HttpStatusCode.OK, loaded.StatusCode);
		Assert.True((await host.UnloadSessionAsync("media-route")).Ok);
		using var unloaded = await Http.GetAsync(url);
		Assert.Equal(HttpStatusCode.NotFound, unloaded.StatusCode);
	}

	private static string MediaUrl(TestHost host, string sessionId, string path) {
		return $"{host.Core.WorkspaceOrigin}/weavie-media/{Uri.EscapeDataString(Path.GetFileName(path))}?token={host.Core.WorkspaceAccessToken}"
			+ $"&session={Uri.EscapeDataString(sessionId)}&path={Uri.EscapeDataString(path)}";
	}
}
