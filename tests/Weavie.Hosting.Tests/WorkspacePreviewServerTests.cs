using System.Net;
using System.Text.Json;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class WorkspacePreviewServerTests {
	private static readonly HttpClient Http = new();

	[Fact]
	public async Task ServesWorkspaceAssetsToAGrantWithoutCredentials() {
		await using var host = await TestHost.StartAsync();
		Directory.CreateDirectory(Path.Combine(host.RepoRoot, "mock"));
		Directory.CreateDirectory(Path.Combine(host.RepoRoot, "shared"));
		await File.WriteAllTextAsync(Path.Combine(host.RepoRoot, "mock", "style.css"), "p{color:red}");
		await File.WriteAllTextAsync(Path.Combine(host.RepoRoot, "shared", "app.js"), "export {}");
		var (grant, baseUrl) = await GrantAsync(host, host.WorkspaceSession, Path.Combine(host.RepoRoot, "mock", "page.html"));
		Assert.EndsWith($"/weavie-preview/{grant}/mock/", baseUrl, StringComparison.Ordinal);

		using var css = await Http.GetAsync(new Uri(new Uri(baseUrl), "style.css"));
		Assert.Equal(HttpStatusCode.OK, css.StatusCode);
		Assert.Equal("p{color:red}", await css.Content.ReadAsStringAsync());
		Assert.Equal("text/css", css.Content.Headers.ContentType?.MediaType);
		Assert.Equal("sandbox allow-scripts", css.Headers.GetValues("Content-Security-Policy").Single());
		Assert.Equal("*", css.Headers.GetValues("Access-Control-Allow-Origin").Single());
		Assert.Equal("nosniff", css.Headers.GetValues("X-Content-Type-Options").Single());

		using var sibling = await Http.GetAsync(new Uri(new Uri(baseUrl), "../shared/app.js"));
		Assert.Equal(HttpStatusCode.OK, sibling.StatusCode);

		host.SessionEvent(host.WorkspaceSession, "files", "previewRelease", new { grant });
		using var released = await Http.GetAsync(new Uri(new Uri(baseUrl), "style.css"));
		Assert.Equal(HttpStatusCode.NotFound, released.StatusCode);
	}

	[Fact]
	public async Task RefusesHiddenSymlinkedAndOutsidePaths() {
		await using var host = await TestHost.StartAsync();
		string outside = Path.Combine(Path.GetDirectoryName(host.RepoRoot)!, $"outside-{Guid.NewGuid():N}");
		Directory.CreateDirectory(outside);
		await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "secret");
		await File.WriteAllTextAsync(Path.Combine(host.RepoRoot, ".env"), "TOKEN=1");
		Directory.CreateSymbolicLink(Path.Combine(host.RepoRoot, "linked"), outside);
		var (_, baseUrl) = await GrantAsync(host, host.WorkspaceSession, Path.Combine(host.RepoRoot, "page.html"));

		foreach (string path in new[] { ".env", ".git/config", "linked/secret.txt", "%2e%2e/" + Path.GetFileName(outside) + "/secret.txt", "missing.css" }) {
			using var response = await Http.GetAsync(baseUrl + path);
			Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{path} answered {response.StatusCode}");
		}

		using var unauthorizedMedia = await Http.GetAsync($"{host.Core.WorkspaceOrigin}/weavie-media/x.png");
		Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedMedia.StatusCode);
		var outsideGrant = await Assert.ThrowsAsync<InvalidOperationException>(
			() => GrantAsync(host, host.WorkspaceSession, Path.Combine(outside, "page.html")));
		Assert.Contains("outside this session's workspace", outsideGrant.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RevokesASessionsGrantsWhenItUnloads() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("preview-route")).Ok);
		var session = host.SelectedSession;
		await File.WriteAllTextAsync(Path.Combine(session.WorkspaceRoot, "pixel.png"), "png");
		var (_, baseUrl) = await GrantAsync(host, session, Path.Combine(session.WorkspaceRoot, "page.html"));

		using var loaded = await Http.GetAsync(baseUrl + "pixel.png");
		Assert.Equal(HttpStatusCode.OK, loaded.StatusCode);
		Assert.True((await host.UnloadSessionAsync("preview-route")).Ok);
		using var unloaded = await Http.GetAsync(baseUrl + "pixel.png");
		Assert.Equal(HttpStatusCode.NotFound, unloaded.StatusCode);
	}

	private static async Task<(string Grant, string Base)> GrantAsync(TestHost host, HostSession session, string path) {
		var granted = await host.SessionRequestAsync<JsonElement>(session, "files", "previewGrant", new { path });
		return (granted.GetProperty("grant").GetString()!, host.Core.WorkspaceOrigin + granted.GetProperty("base").GetString());
	}
}
