using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Sessions;
using Weavie.TestSupport;
using Xunit;
using static Weavie.Hosting.Tests.HostCoreWorkflowTests;

namespace Weavie.Hosting.Tests;

/// <summary>A /btw side session is listed, read, and messaged like any session, through real MCP calls from the fake ACP agent.</summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreSideConversationMessageTests {
	[Theory]
	[InlineData(true, "user-steer")]
	[InlineData(false, "user-message")]
	public async Task ASideSessionMessagesItsBusyParent(bool steering, string delivered) {
		using var state = new TempDirectory("weavie-btw-message");
		await using var host = await TestHost.StartAsync();
		var session = await StartAsync(host, state, steering ? "" : "no-steering");
		session.Agent.Structured!.Submit(AgentTurnSubmission.Prompt("hold"));
		await Eventually(() => File.Exists(Path.Combine(session.WorkspaceRoot, "hold-started")));

		session.Agent.SideConversations!.AskAside(AgentTurnSubmission.Prompt(
			$$"""mcp:messageSession:{"id":"{{session.SlotId}}","message":"finding"}"""));

		await Eventually(() => Pane(host, session).Any(IsToolReply));
		var reply = Pane(host, session).Single(IsToolReply);
		Assert.Equal("messageSession: Delivered to 'btw-parent' (status: working). It replies with messageSession if it has something to report; don't wait for it.", Text(reply));
		var sender = new AgentMessageSender($"btw-parent#{reply.GetProperty("conversationId").GetString()}", "btw parent BTW");
		if (!steering) {
			await Eventually(() => host.Bridge.PostedEvents(session.Address, "agent", "queue").Any(queue =>
				queue.GetProperty("queued").EnumerateArray().Any(queued => Text(queued) == "finding" && Sender(queued) == sender)));
			File.WriteAllText(Path.Combine(session.WorkspaceRoot, "release-hold"), string.Empty);
		}
		await Eventually(() => Pane(host, session).Any(message => Text(message) == "finding"));
		var shown = Assert.Single(Pane(host, session), message => Text(message) == "finding");
		Assert.Equal((JsonValueKind.Null, delivered, sender),
			(shown.GetProperty("conversationId").ValueKind, shown.GetProperty("type").GetString(), Sender(shown)));
	}

	[Fact]
	public async Task ASideSessionIsListedReadAndAnsweredByItsParent() {
		using var state = new TempDirectory("weavie-btw-reply");
		await using var host = await TestHost.StartAsync();
		var session = await StartAsync(host, state, "");
		session.Agent.SideConversations!.AskAside(AgentTurnSubmission.Prompt("mcp:currentSession:{}"));
		await Eventually(() => Pane(host, session).Any(IsToolReply));
		var current = Pane(host, session).Single(IsToolReply);
		string side = $"btw-parent#{current.GetProperty("conversationId").GetString()}";
		Assert.Equal($$"""currentSession: {"id":"{{side}}","parent":"btw-parent"}""", Text(current));
		await Eventually(() => session.Agent.SideConversations.Asides is [{ Running: false }]);

		var listed = await session.Peers.ListAsync(includeSides: true, CancellationToken.None);
		Assert.Equal(
			new SessionPeer(side, "btw parent BTW", "fake-acp", true, "idle", SessionPeerReceive.Ready, "btw-parent", "mcp:currentSession:{}"),
			Assert.Single(listed, peer => peer.Parent is not null));
		Assert.DoesNotContain(await session.Peers.ListAsync(includeSides: false, CancellationToken.None), peer => peer.Parent is not null);

		session.Agent.Structured!.Submit(AgentTurnSubmission.Prompt($$"""mcp:messageSession:{"id":"{{side}}","message":"answer"}"""));

		await Eventually(() => Pane(host, session).Any(message => Text(message) == "echo: answer"));
		var answer = Assert.Single(Pane(host, session), message => Text(message) == "answer");
		Assert.Equal((current.GetProperty("conversationId").GetString(), new AgentMessageSender("btw-parent", "btw parent")),
			(answer.GetProperty("conversationId").GetString(), Sender(answer)));
		var transcript = await session.Peers.ReadAsync(side, 2, CancellationToken.None);
		Assert.Equal((side, "btw parent BTW"), (transcript.Id, transcript.Title));
		Assert.Equal(
			[new(AgentTranscriptRole.User, "answer", null, "btw-parent"), new AgentTranscriptEntry(AgentTranscriptRole.Agent, "echo: answer", "completed", null)],
			transcript.Entries);

		var peers = session.Peers.Side(side[(side.IndexOf('#') + 1)..]);
		Assert.Equal("A session can't message itself.", (await RefusedAsync(() => peers.SendAsync(side, "ping", CancellationToken.None))).Message);
		Assert.Equal("A session can't message itself.", (await RefusedAsync(() => session.Peers.SendAsync("btw-parent", "ping", CancellationToken.None))).Message);
		Assert.Contains("no longer available", (await RefusedAsync(() => session.Peers.SendAsync("btw-parent#gone", "ping", CancellationToken.None))).Message, StringComparison.Ordinal);
		Assert.Contains("No session has id 'btw-parent#gone'", (await RefusedAsync(() => session.Peers.ReadAsync("btw-parent#gone", 1, CancellationToken.None))).Message, StringComparison.Ordinal);
	}

	private static async Task<HostSession> StartAsync(TestHost host, TempDirectory state, string mode) {
		host.Settings.Set(AgentSettings.PaneCoalesceMs, JsonSerializer.SerializeToElement(0L));
		host.RegisterFakeAcp("fake-acp", mode, state.Path);
		Assert.True((await host.CreateSessionAsync(new NewSessionRequest { Branch = "btw-parent", Base = "main", AgentProviderId = "fake-acp" })).Ok);
		var session = host.Session("btw-parent");
		await Eventually(() => session.Agent.Controls!.ControlState.Ready);
		return session;
	}

	private static Task<SessionPeerException> RefusedAsync(Func<Task> send) => Assert.ThrowsAsync<SessionPeerException>(send);

	private static IReadOnlyList<JsonElement> Pane(TestHost host, HostSession session) => host.Bridge.PostedEvents(session.Address, "agent", "pane");

	private static string? Text(JsonElement message) => message.GetProperty("text").GetString();

	private static bool IsToolReply(JsonElement message) => message.GetProperty("type").GetString() == "item-completed"
		&& message.GetProperty("conversationId").ValueKind == JsonValueKind.String
		&& Text(message)?.Contains("Session: ", StringComparison.Ordinal) == true;

	private static AgentMessageSender? Sender(JsonElement message) => message.GetProperty("sender") is { ValueKind: JsonValueKind.Object } sender
		? new(sender.GetProperty("id").GetString()!, sender.GetProperty("title").GetString()!)
		: null;
}
