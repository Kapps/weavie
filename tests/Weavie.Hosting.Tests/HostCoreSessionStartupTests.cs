using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Sessions;
using Weavie.Hosting.Messaging;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreSessionStartupTests {
	[Fact]
	public async Task FreshStructuredSession_PublishesAddressBeforeStartingAgent() {
		await using var host = await TestHost.StartAsync();
		host.Settings.Set(AgentSettings.PaneCoalesceMs, JsonSerializer.SerializeToElement(0L));
		host.Bridge.Clear();

		var result = await host.CreateSessionAsync(new NewSessionRequest {
			Branch = "fresh-codex",
			Base = "main",
			AgentProviderId = "codex",
		});

		Assert.True(result.Ok, result.Error);
		AssertCatalogPrecedesAgentStart(host.Bridge, host.Session("fresh-codex"));
	}

	[Fact]
	public async Task DormantStructuredSession_StartsAgentAgainWhenLoaded() {
		await using var host = await TestHost.StartAsync();
		host.Settings.Set(AgentSettings.PaneCoalesceMs, JsonSerializer.SerializeToElement(0L));
		Assert.True((await host.CreateSessionAsync(new NewSessionRequest {
			Branch = "reload-codex",
			Base = "main",
			AgentProviderId = "codex",
		})).Ok);
		host.SelectSession("primary");
		Assert.True((await host.UnloadSessionAsync("reload-codex")).Ok);
		host.Bridge.Clear();

		var result = await host.InvokeClientCommandAsync(
			SessionCommands.LoadSession,
			new { id = "reload-codex" });

		Assert.True(result.Ok, result.Error);
		AssertCatalogPrecedesAgentStart(host.Bridge, host.Session("reload-codex"));
	}

	private static void AssertCatalogPrecedesAgentStart(FakeHostBridge bridge, HostSession session) {
		var envelopes = bridge.Posted.Select(ParseEnvelope).ToArray();
		int catalog = Array.FindIndex(envelopes, envelope =>
			envelope is { Kind: MessageKind.Event, Feature: "sessions", Name: "catalog" }
			&& CatalogContains(envelope.Payload, session.Address));
		int started = Array.FindIndex(envelopes, envelope =>
			envelope is { Kind: MessageKind.Event, Feature: "agent", Name: "pane" }
			&& envelope.Session == session.Address
			&& envelope.Payload.GetProperty("type").GetString() == "thread-ready");

		Assert.True(catalog >= 0, "The session catalog never published the structured session's exact address.");
		Assert.True(started > catalog, $"The agent start event at {started} did not follow its catalog at {catalog}.");
	}

	private static MessageEnvelope ParseEnvelope(string json) {
		Assert.True(MessageEnvelope.TryParse(json, out var envelope), $"Invalid message envelope: {json}");
		return Assert.IsType<MessageEnvelope>(envelope);
	}

	private static bool CatalogContains(JsonElement catalog, SessionAddress address) =>
		catalog.EnumerateArray().Any(entry =>
			entry.TryGetProperty("address", out var owner)
			&& owner.ValueKind == JsonValueKind.Object
			&& owner.GetProperty("slot").GetString() == address.Slot
			&& owner.GetProperty("incarnation").GetString() == address.Incarnation);
}
