using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Sessions;
using Weavie.TestSupport;
using Xunit;
using static Weavie.Hosting.Tests.HostCoreWorkflowTests;

namespace Weavie.Hosting.Tests;

/// <summary>A /btw side conversation reaches its session's main conversation through messageSession.</summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreSideConversationMessageTests {
	[Theory]
	[InlineData(true, "user-steer")]
	[InlineData(false, "user-message")]
	public async Task ASideConversationMessagesItsBusyMainConversation(bool steering, string delivered) {
		using var state = new TempDirectory("weavie-btw-message");
		await using var host = await TestHost.StartAsync();
		host.Settings.Set(AgentSettings.PaneCoalesceMs, JsonSerializer.SerializeToElement(0L));
		host.RegisterFakeAcp("fake-acp", steering ? "" : "no-steering", state);
		Assert.True((await host.CreateSessionAsync(new NewSessionRequest { Branch = "btw-parent", Base = "main", AgentProviderId = "fake-acp" })).Ok);
		var session = host.Session("btw-parent");
		await Eventually(() => session.Agent.Controls!.ControlState.Ready);
		session.Agent.Structured!.Submit(AgentTurnSubmission.Prompt("hold"));
		await Eventually(() => File.Exists(Path.Combine(session.WorkspaceRoot, "hold-started")));

		session.Agent.SideConversations!.AskAside(AgentTurnSubmission.Prompt(
			$$"""mcp:messageSession:{"id":"{{session.SlotId}}","message":"finding"}"""));

		await Eventually(() => Pane().Any(IsToolReply));
		Assert.Contains("your own session 'btw-parent'", Pane().Single(IsToolReply).GetProperty("text").GetString(), StringComparison.Ordinal);
		if (!steering) {
			await Eventually(() => host.Bridge.PostedEvents(session.Address, "agent", "queue").Any(queue =>
				queue.GetProperty("queued").EnumerateArray().Any(queued => queued.GetProperty("text").GetString() == "finding" && FromThisSession(queued))));
			File.WriteAllText(Path.Combine(session.WorkspaceRoot, "release-hold"), string.Empty);
		}
		await Eventually(() => Pane().Any(IsFinding));
		var shown = Assert.Single(Pane(), IsFinding);
		Assert.True(IsMain(shown));
		Assert.Equal(delivered, shown.GetProperty("type").GetString());
		Assert.True(FromThisSession(shown));

		IReadOnlyList<JsonElement> Pane() => host.Bridge.PostedEvents(session.Address, "agent", "pane");
		static bool IsFinding(JsonElement message) => message.GetProperty("text").GetString() == "finding";
		static bool IsToolReply(JsonElement message) => !IsMain(message) && message.GetProperty("type").GetString() == "item-completed"
			&& message.GetProperty("text").GetString()?.StartsWith("messageSession", StringComparison.Ordinal) == true;
		static bool IsMain(JsonElement message) => message.GetProperty("conversationId").ValueKind == JsonValueKind.Null;
		bool FromThisSession(JsonElement message) => message.GetProperty("sender") is { ValueKind: JsonValueKind.Object } sender
			&& sender.GetProperty("id").GetString() == session.SlotId && sender.GetProperty("sameSession").GetBoolean();
	}
}
