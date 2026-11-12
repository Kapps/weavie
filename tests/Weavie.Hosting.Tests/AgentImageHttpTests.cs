using System.Net;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class AgentImageHttpTests {
	[Fact]
	public async Task Agent_images_answer_their_refusal_as_text_to_the_exact_session_only() {
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;
		using var client = new HttpClient();
		string url = $"{host.Core.WorkspaceOrigin}/weavie-agent-image?slot={Uri.EscapeDataString(session.SlotId)}"
			+ $"&incarnation={session.Incarnation}&url={Uri.EscapeDataString("http://example.com/a.png")}";
		using var unauthenticated = await client.GetAsync(url);
		Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

		string authenticated = $"{url}&token={host.Core.WorkspaceAccessToken}";
		using var request = new HttpRequestMessage(HttpMethod.Get, authenticated);
		request.Headers.Add("Origin", "https://another-workspace.example");
		using var refused = await client.SendAsync(request);
		Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
		Assert.Equal("*", Assert.Single(refused.Headers.GetValues("Access-Control-Allow-Origin")));
		Assert.Equal("Only https images on the default port, without a user name, load.", await refused.Content.ReadAsStringAsync());

		using var stale = await client.GetAsync(authenticated.Replace(session.Incarnation, "stale", StringComparison.Ordinal));
		Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
	}

	[Fact]
	public async Task Token_media_reads_are_open_to_another_origin_for_cropping() {
		await using var host = await TestHost.StartAsync();
		using var client = new HttpClient();
		using var request = new HttpRequestMessage(HttpMethod.Get,
			$"{host.Core.WorkspaceOrigin}/weavie-media/a.png?session={host.SelectedSession.Incarnation}&path=/missing/a.png&token={host.Core.WorkspaceAccessToken}");
		request.Headers.Add("Origin", "https://another-workspace.example");

		using var response = await client.SendAsync(request);

		Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
	}
}
