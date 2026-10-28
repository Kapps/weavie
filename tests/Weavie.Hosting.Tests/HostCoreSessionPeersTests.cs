using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Mcp;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreSessionPeersTests {
	[Fact]
	public async Task AMessageReachesTheTargetAgentAttributedToTheSender() {
		await using var host = await TestHost.StartAsync();
		host.Settings.Set(AgentSettings.PaneCoalesceMs, JsonSerializer.SerializeToElement(0L));
		var sender = await CreateStructuredAsync(host, "peer-sender");
		var target = await CreateStructuredAsync(host, "peer-target");

		var delivery = await sender.Peers.SendAsync(target.SlotId, "ping", CancellationToken.None);
		await target.Agent.DrainPaneAsync(CancellationToken.None);

		Assert.Equal("peer-target", delivery.Id);
		string framed = EmbeddedAgentGuidanceText("peer-sender", "ping");
		Assert.Contains(
			host.Bridge.PostedEvents(target.Address, "agent", "pane"),
			message => message.GetProperty("type").GetString() == "item-completed"
				&& message.GetProperty("text").GetString() == $"echo: {framed}");
		Assert.DoesNotContain(
			host.Bridge.PostedEvents(sender.Address, "agent", "pane"),
			message => message.GetProperty("text").GetString()?.Contains("ping", StringComparison.Ordinal) == true);

		var transcript = await sender.Peers.ReadAsync(target.SlotId, 2, CancellationToken.None);
		Assert.Equal(
			[new(AgentTranscriptRole.User, framed, null), new AgentTranscriptEntry(AgentTranscriptRole.Agent, $"echo: {framed}", "completed")],
			transcript.Entries);
	}

	[Fact]
	public async Task SessionsThatCantReceiveRefuseAndListSaysWhy() {
		await using var host = await TestHost.StartAsync();
		var sender = await CreateStructuredAsync(host, "peer-sender");
		await CreateStructuredAsync(host, "peer-dormant");
		var unloaded = await host.InvokeCommandAsync(
			sender.SlotId, SessionCommands.UnloadSession, new { id = "peer-dormant" }, CancellationToken.None);
		Assert.True(unloaded.Ok, unloaded.Error);
		string claude = host.WorkspaceSession.SlotId;

		var peers = (await sender.Peers.ListAsync(CancellationToken.None)).ToDictionary(peer => peer.Id, peer => peer.Receive);
		Assert.Equal(SessionPeerReceive.Ready, peers["peer-sender"]);
		Assert.Equal(SessionPeerReceive.NeedsLoad, peers["peer-dormant"]);
		Assert.Equal(SessionPeerReceive.Unsupported, peers[claude]);

		await AssertRefusedAsync(sender, "peer-sender", "can't message itself");
		await AssertRefusedAsync(sender, "no-such-session", "No session has id");
		await AssertRefusedAsync(sender, "peer-dormant", "weavie.session.load");
		await AssertRefusedAsync(sender, claude, "in a terminal");
	}

	[Fact]
	public async Task AnUnloadedSessionCanStillBeRead() {
		await using var host = await TestHost.StartAsync();
		var reader = await CreateStructuredAsync(host, "peer-reader");
		var dormant = await CreateStructuredAsync(host, "peer-history");
		host.SessionEvent(dormant, "agent", "submit", new {
			id = "turn-1",
			prompt = "remember me",
			kind = "prompt",
			commandName = "",
			attachmentIds = Array.Empty<string>(),
		});
		var unloaded = await host.InvokeCommandAsync(
			reader.SlotId, SessionCommands.UnloadSession, new { id = "peer-history" }, CancellationToken.None);
		Assert.True(unloaded.Ok, unloaded.Error);

		var transcript = await reader.Peers.ReadAsync("peer-history", 1, CancellationToken.None);

		Assert.Equal(2, transcript.Total);
		Assert.Equal("echo: remember me", Assert.Single(transcript.Entries).Text);
	}

	private static string EmbeddedAgentGuidanceText(string sender, string body) =>
		EmbeddedAgentGuidance.SessionMessage(sender, SessionName.Title(sender), body);

	private static async Task AssertRefusedAsync(HostSession sender, string target, string reason) {
		var refused = await Assert.ThrowsAsync<SessionPeerException>(
			() => sender.Peers.SendAsync(target, "ping", CancellationToken.None));
		Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
	}

	private static async Task<HostSession> CreateStructuredAsync(TestHost host, string branch) {
		var created = await host.InvokeCommandAsync(
			host.WorkspaceSession.SlotId,
			SessionCommands.NewSession,
			new NewSessionRequest { Branch = branch, Base = "main", AgentProviderId = "structured" },
			CancellationToken.None);
		Assert.True(created.Ok, created.Error);
		return host.Session(branch);
	}
}
