using System.Net;
using System.Text.Json;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class AgentHistoryHttpTests {
	[Fact]
	public async Task History_requires_authentication_and_exact_session_and_validates_its_baseline() {
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;
		using var client = new HttpClient();
		string url = $"{host.Core.WorkspaceOrigin}/weavie-agent-history?slot={Uri.EscapeDataString(session.SlotId)}"
			+ $"&incarnation={session.Incarnation}";
		using var unauthenticated = await client.GetAsync(url);
		Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

		string authenticated = $"{url}&token={host.Core.WorkspaceAccessToken}";
		using var request = new HttpRequestMessage(HttpMethod.Get, authenticated);
		request.Headers.Add("Origin", "https://another-workspace.example");
		using var response = await client.SendAsync(request);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
		Assert.True(response.Headers.CacheControl!.NoStore);
		string body = await response.Content.ReadAsStringAsync();
		var completion = JsonSerializer.Deserialize<JsonElement>(body.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]);
		Assert.True(completion.GetProperty("complete").GetBoolean());

		using var stale = await client.GetAsync(authenticated.Replace(session.Incarnation, "stale", StringComparison.Ordinal));
		Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
		using var invalid = await client.GetAsync(authenticated + "&knownGeneration=0");
		Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
	}

	[Fact]
	public async Task Cookie_only_history_cannot_be_read_from_another_origin() {
		await using var host = await TestHost.StartAsync();
		using var client = new HttpClient();
		using var bootstrap = await client.GetAsync(host.Core.WorkspaceNativePageUrl);
		var session = host.SelectedSession;
		string url = $"{host.Core.WorkspaceOrigin}/weavie-agent-history?slot={Uri.EscapeDataString(session.SlotId)}"
			+ $"&incarnation={session.Incarnation}";
		using var sameOrigin = await client.GetAsync(url);
		Assert.Equal(HttpStatusCode.OK, sameOrigin.StatusCode);
		using var request = new HttpRequestMessage(HttpMethod.Get, url);
		request.Headers.Add("Origin", "https://another-workspace.example");
		using var denied = await client.SendAsync(request);
		Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
		Assert.False(denied.Headers.Contains("Access-Control-Allow-Origin"));
	}
}
