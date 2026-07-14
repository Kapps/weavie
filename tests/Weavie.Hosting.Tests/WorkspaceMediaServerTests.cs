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
		string url = MediaUrl(host, host.PrimaryId, path);

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
	public async Task RequiresTheServerTokenAndHidesEveryOutOfScopePathAsNotFound() {
		await using var host = await TestHost.StartAsync();
		string inside = Path.Combine(host.RepoRoot, "pixel.png");
		string html = Path.Combine(host.RepoRoot, "page.html");
		string svg = Path.Combine(host.RepoRoot, "active.svg");
		await File.WriteAllBytesAsync(inside, [1, 2, 3]);
		await File.WriteAllTextAsync(html, "<script>window.top.pwned = true</script>");
		await File.WriteAllTextAsync(svg, "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
		var page = new Uri(host.Core.WorkspacePageUrl);
		string noToken = $"{host.Core.WorkspaceOrigin}/weavie-media?session={host.PrimaryId}&path={Uri.EscapeDataString(inside)}";

		using var unauthorized = await Http.GetAsync(noToken);
		Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

		string outside = Path.Combine(Path.GetDirectoryName(host.RepoRoot)!, "outside.png");
		await File.WriteAllBytesAsync(outside, [4, 5, 6]);
		using var direct = await Http.GetAsync(MediaUrl(host, host.PrimaryId, outside));
		using var traversal = await Http.GetAsync(MediaUrl(
			host,
			host.PrimaryId,
			Path.Combine(host.RepoRoot, "..", Path.GetFileName(outside))));
		using var wrongSession = await Http.GetAsync(MediaUrl(host, "not-loaded", inside));
		using var activeHtml = await Http.GetAsync(MediaUrl(host, host.PrimaryId, html));
		using var activeSvg = await Http.GetAsync(MediaUrl(host, host.PrimaryId, svg));
		string malformed = $"{host.Core.WorkspaceOrigin}/weavie-media?{page.Query.TrimStart('?')}"
			+ $"&session={Uri.EscapeDataString(host.PrimaryId)}&path=%00";
		using var malformedPath = await Http.GetAsync(malformed);

		Assert.Equal(HttpStatusCode.NotFound, direct.StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, traversal.StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, wrongSession.StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, activeHtml.StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, activeSvg.StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, malformedPath.StatusCode);
		if (File.Exists("/etc/passwd")) {
			using var systemFile = await Http.GetAsync(MediaUrl(host, host.PrimaryId, "/etc/passwd"));
			Assert.Equal(HttpStatusCode.NotFound, systemFile.StatusCode);
		}
		Assert.Equal("token", page.Query.Split('=')[0].TrimStart('?'));
	}

	[Fact]
	public async Task ExposesOnlyTheExactSessionsScratchAndPastedImageRoots() {
		await using var host = await TestHost.StartAsync();
		var session = host.Core.ActiveSessionForTest()!;
		string scratch = Path.Combine(session.Scratch.Directory, "generated.png");
		string pasted = Path.Combine(session.PastedImages.Directory, "paste-1.png");
		Directory.CreateDirectory(session.Scratch.Directory);
		Directory.CreateDirectory(session.PastedImages.Directory);
		await File.WriteAllBytesAsync(scratch, [7, 8]);
		await File.WriteAllBytesAsync(pasted, [9, 10]);

		using var scratchResponse = await Http.GetAsync(MediaUrl(host, session.Id, scratch));
		using var pastedResponse = await Http.GetAsync(MediaUrl(host, session.Id, pasted));

		Assert.Equal(HttpStatusCode.OK, scratchResponse.StatusCode);
		Assert.Equal(HttpStatusCode.OK, pastedResponse.StatusCode);
	}

	[Fact]
	public async Task UnregistersASecondarySessionsRouteBeforeItsBackendIsDisposed() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("media-route")).Ok);
		var session = host.Core.ActiveSessionForTest()!;
		string path = Path.Combine(session.WorkspaceRoot, "session.png");
		await File.WriteAllBytesAsync(path, [11, 12]);
		string url = MediaUrl(host, session.Id, path);

		using var loaded = await Http.GetAsync(url);
		Assert.Equal(HttpStatusCode.OK, loaded.StatusCode);
		Assert.True((await host.Core.UnloadSessionAsync(sessionId: null, CancellationToken.None)).Ok);
		using var unloaded = await Http.GetAsync(url);
		Assert.Equal(HttpStatusCode.NotFound, unloaded.StatusCode);
	}

	private static string MediaUrl(TestHost host, string sessionId, string path) {
		string token = new Uri(host.Core.WorkspacePageUrl).Query.TrimStart('?');
		return $"{host.Core.WorkspaceOrigin}/weavie-media?{token}"
			+ $"&session={Uri.EscapeDataString(sessionId)}&path={Uri.EscapeDataString(path)}";
	}
}
