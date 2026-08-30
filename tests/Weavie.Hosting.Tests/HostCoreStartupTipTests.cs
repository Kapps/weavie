using System.Text.Json;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreStartupTipTests {
	[Fact]
	public async Task Hello_OffersOneStartupTipPerHostLifetime() {
		await using var host = await TestHost.StartAsync();

		var tip = Assert.Single(host.Bridge.PostedEvents("tips", "show"));
		Assert.False(string.IsNullOrWhiteSpace(tip.GetProperty("id").GetString()));
		Assert.False(string.IsNullOrWhiteSpace(tip.GetProperty("lead").GetString()));
		Assert.False(string.IsNullOrWhiteSpace(tip.GetProperty("detail").GetString()));

		await host.HostRequestAsync<JsonElement>("connection", "hello", new { });

		Assert.Single(host.Bridge.PostedEvents("tips", "show"));
	}
}
