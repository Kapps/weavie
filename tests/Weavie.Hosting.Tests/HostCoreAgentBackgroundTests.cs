using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Hosting.Tests;

// Actions that stop running background work refuse with the work listed until the caller asks to stop it.
public sealed class HostCoreAgentBackgroundTests {
	[Theory]
	[InlineData(CoreCommands.RestartAgent)]
	[InlineData(CoreCommands.ClearAgentConversation)]
	[InlineData(SessionCommands.UnloadSession)]
	public async Task StoppingActionsRefuseListingRunningWorkUntilFlagged(string command) {
		await using var host = await TestHost.StartAsync();
		var session = await StartBackgroundAsync(host, "guarded");

		var refused = await host.InvokeCommandAsync(session.SlotId, command, new { id = session.SlotId }, CancellationToken.None);

		Assert.False(refused.Ok);
		var work = Assert.Single(JsonDocument.Parse(refused.DataJson!).RootElement.GetProperty("backgroundWork").EnumerateArray());
		Assert.Equal(("sleep 30", "shell", "running", 1L), (work.GetProperty("name").GetString(), work.GetProperty("type").GetString(),
			work.GetProperty("state").GetString(), work.GetProperty("startedAtMs").GetInt64()));
		Assert.True((await host.InvokeCommandAsync(session.SlotId, command,
			new { id = session.SlotId, stopBackgroundWork = true }, CancellationToken.None)).Ok);
	}

	[Fact]
	public async Task StopCommandStopsTheMostRecentStoppableTaskAndThenNothingRefuses() {
		await using var host = await TestHost.StartAsync();
		var session = await StartBackgroundAsync(host, "stoppable");

		Assert.True((await host.InvokeCommandAsync(session.SlotId, CoreCommands.StopBackgroundTask, new { }, CancellationToken.None)).Ok);

		Assert.True((await host.InvokeCommandAsync(session.SlotId, CoreCommands.RestartAgent, new { }, CancellationToken.None)).Ok);
		Assert.False((await host.InvokeCommandAsync(session.SlotId, CoreCommands.StopBackgroundTask, new { }, CancellationToken.None)).Ok);
	}

	private static async Task<HostSession> StartBackgroundAsync(TestHost host, string branch) {
		Assert.True((await host.CreateSessionAsync(new NewSessionRequest { Branch = branch, Base = "main", AgentProviderId = "structured" })).Ok);
		var session = host.Session(branch);
		host.SessionEvent(session, "agent", "submit",
			new { id = "", prompt = FakeStructuredAgentProvider.BackgroundPrompt, kind = "prompt", commandName = "", attachmentIds = Array.Empty<string>() });
		Assert.True(session.Agent.Background!.BackgroundWork.Single().Running);
		return session;
	}
}
