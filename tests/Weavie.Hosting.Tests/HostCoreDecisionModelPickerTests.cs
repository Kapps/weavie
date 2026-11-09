using System.Net;
using System.Text.Json;
using Weavie.Core.Configuration;
using Xunit;
using static Weavie.Hosting.Tests.HostCoreCredentialsTests;

namespace Weavie.Hosting.Tests;

// Choose Decision Model… over the real host: the picker, connecting a vendor before choosing it, and the reset when the
// chosen vendor is disconnected.
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreDecisionModelPickerTests {
	private const string Choose = "weavie.inference.chooseDecisionProvider";

	[Fact]
	public async Task Picker_MarksTheCurrentChoiceAndListsConnectedVendorsBeforeUnconnectedOnes() {
		await using var host = await TestHost.StartAsync();
		host.SaveCredential("typesafe", "key");

		Assert.True((await host.InvokeClientCommandAsync(Choose, new { })).Ok);

		var picker = await Wait.ForAsync(() => Picker(host));
		Assert.Equal("agent", picker.GetProperty("value").GetString());
		var options = picker.GetProperty("options").EnumerateArray().ToList();
		Assert.Equal(["agent", "typesafe", "openai"], options.Select(option => option.GetProperty("id").GetString()));
		Assert.Equal([JsonValueKind.Null, JsonValueKind.String, JsonValueKind.String], options.Select(option => option.GetProperty("group").ValueKind));
		Assert.Equal(["Connected", "Not connected"], options.Skip(1).Select(option => option.GetProperty("group").GetString()));
	}

	[Fact]
	public async Task PickingAnUnconnectedVendor_ChoosesItOnlyOnceItsKeyIsSaved() {
		await using var host = await TestHost.StartAsync();
		host.Http.Responder = request => request.Headers.Authorization!.Parameter == "sk-good"
			? (HttpStatusCode.OK, """{"data":[]}""")
			: (HttpStatusCode.Unauthorized, "{}");
		Assert.True((await host.InvokeClientCommandAsync(Choose, new { })).Ok);
		await Wait.ForAsync(() => Picker(host));

		host.SessionEvent(host.SelectedSession, "picker", "pick", new { id = "openai" });

		Assert.Equal("openai", (await Wait.ForAsync(() => Prompt(host))).GetProperty("id").GetString());
		Assert.Equal("agent", Current(host));
		Assert.False((await SaveKey(host, "openai", "sk-bad")).GetProperty("ok").GetBoolean());
		Assert.Equal("agent", Current(host));
		Assert.True((await SaveKey(host, "openai", "sk-good")).GetProperty("ok").GetBoolean());
		Assert.Equal("openai", Current(host));
		await Wait.UntilAsync(() => host.Bridge.PostedEvents(host.SelectedSession.Address, "notifications", "show").Any(toast =>
			toast.GetProperty("message").GetString()!.Contains("ad-hoc inference is off — turn on inference.enabled", StringComparison.Ordinal)));
	}

	[Fact]
	public async Task ChoosingAConnectedVendorOrTheAgent_SetsItAtOnce() {
		await using var host = await TestHost.StartAsync();
		host.Settings.Set(InferenceSettings.Enabled, JsonSerializer.SerializeToElement(true));
		host.SaveCredential("typesafe", "key");

		var vendor = await host.InvokeClientCommandAsync(Choose, new { provider = "typesafe" });
		Assert.Equal((true, "Workflow decisions now use TypeSafe."), (vendor.Ok, vendor.Message));
		Assert.Equal("typesafe", Current(host));

		var agent = await host.InvokeClientCommandAsync(Choose, new { provider = "agent" });
		Assert.Equal((true, "Workflow decisions now use Agent."), (agent.Ok, agent.Message));
		Assert.Equal("agent", Current(host));
		Assert.False((await host.InvokeClientCommandAsync(Choose, new { provider = "gemini" })).Ok);
		Assert.Null(Prompt(host));
	}

	[Fact]
	public async Task DisconnectingTheChosenVendor_HandsDecisionsBackToTheAgent() {
		await using var host = await TestHost.StartAsync();
		host.SaveCredential("openai", "key");
		host.SaveCredential("typesafe", "key");
		Assert.True((await host.InvokeClientCommandAsync(Choose, new { provider = "typesafe" })).Ok);

		Assert.True((await host.InvokeClientCommandAsync("weavie.credentials.disconnect", new { service = "openai" })).Ok);
		Assert.Equal("typesafe", Current(host));

		var result = await host.InvokeClientCommandAsync("weavie.credentials.disconnect", new { service = "typesafe" });
		Assert.Equal("agent", Current(host));
		Assert.StartsWith("Disconnected TypeSafe. Workflow decisions will use Agent", result.Message, StringComparison.Ordinal);
	}

	private static string Current(TestHost host) => host.Settings.RequireString(InferenceSettings.DecisionProvider);
}
