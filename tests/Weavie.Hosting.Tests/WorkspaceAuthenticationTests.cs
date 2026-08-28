using System.Net;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class WorkspaceAuthenticationTests {
	[Fact]
	public async Task Native_server_clean_page_requires_a_connection_cookie() {
		await using var host = await TestHost.StartAsync();
		using var client = new HttpClient();

		var response = await client.GetAsync(host.Core.WorkspacePageUrl);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Contains("Connect to Weavie", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Connection_page_accepts_a_fragment_token_under_a_nonce_policy() {
		await using var host = await TestHost.StartAsync();
		using var client = new HttpClient();

		var response = await client.GetAsync(host.Core.WorkspacePageUrl);
		string document = await response.Content.ReadAsStringAsync();
		string policy = response.Headers.GetValues("Content-Security-Policy").Single();

		Assert.Contains("script-src 'nonce-", policy, StringComparison.Ordinal);
		Assert.Contains("new URLSearchParams(location.hash.slice(1))", document, StringComparison.Ordinal);
		Assert.Contains("history.replaceState", document, StringComparison.Ordinal);
		Assert.Contains("input.form.requestSubmit()", document, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Native_bootstrap_establishes_a_cookie_then_redirects_clean() {
		await using var host = await TestHost.StartAsync();
		var cookies = new CookieContainer();
		using var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = cookies };
		using var client = new HttpClient(handler);

		var bootstrap = await client.GetAsync(host.Core.WorkspaceNativePageUrl);

		Assert.Equal(HttpStatusCode.Redirect, bootstrap.StatusCode);
		Assert.Equal("/index.html", bootstrap.Headers.Location?.OriginalString);
		Assert.Contains(bootstrap.Headers.GetValues("Set-Cookie"), value =>
			value.Contains("HttpOnly", StringComparison.OrdinalIgnoreCase)
			&& value.Contains("SameSite=Strict", StringComparison.OrdinalIgnoreCase));
		var document = await client.GetAsync(host.Core.WorkspacePageUrl);
		Assert.Equal(HttpStatusCode.OK, document.StatusCode);
		Assert.DoesNotContain("Connect to Weavie", await document.Content.ReadAsStringAsync(), StringComparison.Ordinal);
		Assert.Empty(new Uri(host.Core.WorkspacePageUrl).Query);
	}

	[Fact]
	public async Task Only_cross_origin_dev_bootstrap_carries_a_media_transport_token() {
		await using var host = await TestHost.StartAsync();

		Assert.DoesNotContain("__WEAVIE_RESOURCE_BASE__", host.Core.BuildBootstrap(), StringComparison.Ordinal);
		Assert.Contains("weavie-media?token=", host.Core.BuildCrossOriginBootstrap(), StringComparison.Ordinal);
	}
}
