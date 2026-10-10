using System.Text.Json;
using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreInferenceControlsTests {
	[Fact]
	public async Task OpenAnswersAndPushesTheProvidersAnswerAndEveryChange() {
		await using var host = await TestHost.StartAsync();
		// The structured fake can't answer inference queries, so the ask fails deterministically and says why.
		host.Settings.Set(InferenceSettings.DefaultProvider, JsonSerializer.SerializeToElement("structured"));

		var opened = await host.HostRequestAsync<JsonElement>("inferenceControls", "open", new { });

		var agent = opened.GetProperty("axes")[0];
		Assert.Equal(InferenceSettings.DefaultProvider, agent.GetProperty("id").GetString());
		Assert.Contains("claude", agent.GetProperty("options").EnumerateArray().Select(option => option.GetProperty("id").GetString()));
		Assert.Equal("Agent 'structured' isn't installed.", opened.GetProperty("warning").GetString());
		var failed = await WaitForState(host, state => state.GetProperty("status").GetString() == "failed");
		Assert.Contains("does not support ad-hoc inference", failed.GetProperty("error").GetString(), StringComparison.Ordinal);

		host.Settings.Set(InferenceSettings.Enabled, JsonSerializer.SerializeToElement(true));

		await WaitForState(host, state => state.GetProperty("enabled").GetBoolean());
	}

	private static async Task<JsonElement> WaitForState(TestHost host, Func<JsonElement, bool> matches) {
		for (int attempt = 0; attempt < 500; attempt++) {
			if (host.Bridge.LastEvent("inferenceControls", "state") is { } state && matches(state)) return state;
			await Task.Delay(10);
		}
		throw new TimeoutException("The host never pushed the expected suggestion controls.");
	}
}
