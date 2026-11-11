using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
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
		var shown = Assert.Single(
			host.Bridge.PostedEvents(target.Address, "agent", "pane"),
			message => message.GetProperty("type").GetString() == "user-message");
		Assert.Equal("ping", shown.GetProperty("text").GetString());
		Assert.Equal(("peer-sender", "peer sender", false), Sender(shown));
		Assert.DoesNotContain(
			host.Bridge.PostedEvents(sender.Address, "agent", "pane"),
			message => message.GetProperty("text").GetString()?.Contains("ping", StringComparison.Ordinal) == true);

		var transcript = await sender.Peers.ReadAsync(target.SlotId, 2, CancellationToken.None);
		Assert.Equal(
			[new(AgentTranscriptRole.User, "ping", null, "peer-sender"), new AgentTranscriptEntry(AgentTranscriptRole.Agent, "echo: ping", "completed", null)],
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

		await AssertRefusedAsync(sender, "no-such-session", "No session has id");
		await AssertRefusedAsync(sender, "peer-dormant", "weavie.session.load");
		await AssertRefusedAsync(sender, claude, "in a terminal");
	}

	[Fact]
	public async Task ItsOwnIdReachesTheSessionsMainConversation() {
		await using var host = await TestHost.StartAsync();
		host.Settings.Set(AgentSettings.PaneCoalesceMs, JsonSerializer.SerializeToElement(0L));
		var session = await CreateStructuredAsync(host, "peer-own");

		var delivery = await session.Peers.SendAsync(session.SlotId, "finding", CancellationToken.None);
		await session.Agent.DrainPaneAsync(CancellationToken.None);

		Assert.Equal("peer-own", delivery.Id);
		var shown = Assert.Single(
			host.Bridge.PostedEvents(session.Address, "agent", "pane"),
			message => message.GetProperty("type").GetString() == "user-message");
		Assert.Equal(("finding", ("peer-own", "peer own", true)), (shown.GetProperty("text").GetString(), Sender(shown)));
		var transcript = await session.Peers.ReadAsync(session.SlotId, 2, CancellationToken.None);
		Assert.Equal(new AgentTranscriptEntry(AgentTranscriptRole.User, "finding", null, "peer-own"), transcript.Entries[0]);
	}

	[Fact]
	public async Task ASendFromAnIncarnationThatUnloadedIsRefused() {
		await using var host = await TestHost.StartAsync();
		host.Settings.Set(AgentSettings.PaneCoalesceMs, JsonSerializer.SerializeToElement(0L));
		var target = await CreateStructuredAsync(host, "peer-target");
		var stale = (await CreateStructuredAsync(host, "peer-reloaded")).Peers;
		foreach (string command in new[] { SessionCommands.UnloadSession, SessionCommands.LoadSession }) {
			var result = await host.InvokeCommandAsync(target.SlotId, command, new { id = "peer-reloaded" }, CancellationToken.None);
			Assert.True(result.Ok, result.Error);
		}
		var reloaded = host.Session("peer-reloaded");

		foreach (string id in new[] { "peer-reloaded", "peer-target" }) {
			var refused = await Assert.ThrowsAsync<SessionPeerException>(() => stale.SendAsync(id, "ping", CancellationToken.None));
			Assert.Equal("The sending session no longer exists.", refused.Message);
		}
		await reloaded.Agent.DrainPaneAsync(CancellationToken.None);
		await target.Agent.DrainPaneAsync(CancellationToken.None);
		Assert.DoesNotContain(
			host.Bridge.PostedEvents(reloaded.Address, "agent", "pane").Concat(host.Bridge.PostedEvents(target.Address, "agent", "pane")),
			message => message.GetProperty("text").GetString() == "ping");
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

	private static (string? Id, string? Title, bool SameSession) Sender(JsonElement message) {
		var sender = message.GetProperty("sender");
		return (sender.GetProperty("id").GetString(), sender.GetProperty("title").GetString(), sender.GetProperty("sameSession").GetBoolean());
	}

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
