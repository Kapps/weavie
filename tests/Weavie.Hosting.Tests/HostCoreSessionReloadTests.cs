using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Hosting.Messaging;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreSessionReloadTests {
	private static IEnumerable<JsonElement> CatalogEntries(TestHost host, string id) =>
		host.Bridge.PostedEvents("sessions", "catalog")
			.SelectMany(catalog => catalog.EnumerateArray())
			.Where(entry => entry.GetProperty("id").GetString() == id);

	[Fact]
	public async Task ReloadSwapsTheIncarnationWithoutEverGoingDormantOrFlushing() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);
		string before = host.Session("feature").Address.Incarnation;
		host.Bridge.Clear();

		var result = await host.InvokeCommandAsync(host.SelectedSession.SlotId, SessionCommands.ReloadSession, new { id = "feature" }, CancellationToken.None);

		Assert.True(result.Ok, result.Error);
		Assert.NotEqual(before, host.Session("feature").Address.Incarnation);
		Assert.NotEmpty(CatalogEntries(host, "feature"));
		Assert.All(CatalogEntries(host, "feature"), entry => Assert.NotEqual(JsonValueKind.Null, entry.GetProperty("address").ValueKind));
		Assert.DoesNotContain(
			host.Bridge.Posted.Select(json => MessageEnvelope.TryParse(json, out var envelope) ? envelope : null),
			envelope => envelope is { Kind: MessageKind.Request, Feature: "editor", Name: "flush" });
	}

	[Fact]
	public async Task ACatalogCarriesNoReloadRequirementForAHealthySession() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);

		Assert.All(CatalogEntries(host, "feature"), entry => Assert.Equal(JsonValueKind.Null, entry.GetProperty("reloadRequired").ValueKind));
	}
}
