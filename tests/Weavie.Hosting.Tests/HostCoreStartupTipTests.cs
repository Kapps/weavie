using System.Text.Json;
using Weavie.Core.Configuration;
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

	[Fact]
	public async Task Hello_AsksForGettingStarted_OnlyWhileSetupIsUnfinished() {
		await using var host = await TestHost.StartAsync();
		Assert.Empty(host.Bridge.PostedEvents("gettingStarted", "show"));

		host.Settings.Set(CoreSettings.GettingStartedCompleted, JsonSerializer.SerializeToElement(false));
		await host.HostRequestAsync<JsonElement>("connection", "hello", new { });

		Assert.Single(host.Bridge.PostedEvents("gettingStarted", "show"));
	}
}
