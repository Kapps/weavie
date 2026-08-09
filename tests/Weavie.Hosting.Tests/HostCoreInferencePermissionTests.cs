using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Hosting.Inference;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreInferencePermissionTests {
	[Fact]
	public async Task Hello_OffersAutomaticInferenceOnceWithACommandAction() {
		await using var host = await TestHost.StartAsync();

		var offer = Assert.Single(Offers(host));
		Assert.Equal("info", offer.GetProperty("level").GetString());
		Assert.Contains("may use tokens", offer.GetProperty("message").GetString(), StringComparison.Ordinal);
		var action = offer.GetProperty("action");
		Assert.Equal("Allow", action.GetProperty("label").GetString());
		Assert.Equal(CoreCommands.EnableAutomaticInference, action.GetProperty("commandId").GetString());

		await host.HostRequestAsync<JsonElement>("connection", "hello", new { });

		Assert.Single(Offers(host));
	}

	[Theory]
	[InlineData(InferenceSettings.Enabled)]
	[InlineData(InferenceSettings.AllowAutomatic)]
	public async Task Hello_StillOffersWhenOnlyOneGateIsEnabled(string key) {
		await using var host = await StartWithSettingsAsync(settings => SetTrue(settings, key));

		Assert.Single(Offers(host));
	}

	[Fact]
	public async Task Hello_DoesNotOfferWhenAutomaticInferenceIsEnabled() {
		await using var host = await StartWithSettingsAsync(settings => {
			SetTrue(settings, InferenceSettings.AllowAutomatic);
			SetTrue(settings, InferenceSettings.Enabled);
		});

		Assert.Empty(Offers(host));
	}

	[Fact]
	public async Task OfferCommand_EnablesBothPolicyGatesWithoutALiveSession() {
		await using var host = await TestHost.StartAsync();
		string id = host.WorkspaceSession.SlotId;
		var unloaded = await host.HostRequestAsync<JsonElement>(
			"sessions",
			"invoke",
			new { id = SessionCommands.UnloadSession, args = new { id } });
		Assert.True(unloaded.GetProperty("ok").GetBoolean());

		var result = await host.HostRequestAsync<JsonElement>(
			"commands",
			"invoke",
			new { id = CoreCommands.EnableAutomaticInference, args = new { } });

		Assert.True(result.GetProperty("ok").GetBoolean());
		Assert.True(host.Settings.RequireBool(InferenceSettings.AllowAutomatic));
		Assert.True(host.Settings.RequireBool(InferenceSettings.Enabled));
		Assert.Equal(
			"inference-automatic-opt-in",
			host.Bridge.LastEvent("notifications", "clear")?.GetProperty("key").GetString());
	}

	private static IReadOnlyList<JsonElement> Offers(TestHost host) => host.Bridge
		.PostedEvents("notifications", "show")
		.Where(message => message.TryGetProperty("key", out var key)
			&& key.GetString() == "inference-automatic-opt-in")
		.ToArray();

	private static Task<TestHost> StartWithSettingsAsync(Action<SettingsStore> configure) =>
		TestHost.StartAsync(
			_ => { },
			settings => {
				configure(settings);
				return InferenceComposition.CreateDisabled(settings);
			});

	private static void SetTrue(SettingsStore settings, string key) =>
		settings.Set(key, JsonSerializer.SerializeToElement(true));
}
