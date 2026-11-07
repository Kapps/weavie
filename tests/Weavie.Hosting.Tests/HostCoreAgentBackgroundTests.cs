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
		var data = JsonDocument.Parse(refused.DataJson!).RootElement;
		Assert.Equal((session.SlotId, session.Incarnation),
			(data.GetProperty("session").GetProperty("slot").GetString(), data.GetProperty("session").GetProperty("incarnation").GetString()));
		var work = Assert.Single(data.GetProperty("backgroundWork").EnumerateArray());
		Assert.Equal(("sleep 30", "shell", "running", 1L), (work.GetProperty("name").GetString(), work.GetProperty("type").GetString(),
			work.GetProperty("state").GetString(), work.GetProperty("startedAtMs").GetInt64()));
		Assert.True((await host.InvokeCommandAsync(session.SlotId, command,
			new { id = session.SlotId, stopBackgroundWork = true }, CancellationToken.None)).Ok);
	}

	// /finish-session deletes its own session from the turn a finished CI watch started.
	[Theory]
	[InlineData(SessionCommands.UnloadSession)]
	[InlineData(SessionCommands.DeleteSession)]
	public async Task TheAgentsOwnUnpromptedTurnNeverRefuses(string command) {
		await using var host = await TestHost.StartAsync();
		var session = await StartBackgroundAsync(host, "unprompted", FakeStructuredAgentProvider.AutonomousTurnPrompt);

		var result = await host.InvokeCommandAsync(session.SlotId, command, new { id = session.SlotId, force = true }, CancellationToken.None);

		Assert.True(result.Ok, result.Error);
	}

	[Fact]
	public async Task StopCommandStopsTheMostRecentStoppableTaskAndThenNothingRefuses() {
		await using var host = await TestHost.StartAsync();
		var session = await StartBackgroundAsync(host, "stoppable");

		Assert.True((await host.InvokeCommandAsync(session.SlotId, CoreCommands.StopBackgroundTask, new { }, CancellationToken.None)).Ok);

		Assert.True((await host.InvokeCommandAsync(session.SlotId, CoreCommands.RestartAgent, new { }, CancellationToken.None)).Ok);
		Assert.False((await host.InvokeCommandAsync(session.SlotId, CoreCommands.StopBackgroundTask, new { }, CancellationToken.None)).Ok);
	}

	[Fact]
	public async Task RestartReportsSuccessOnlyOnceTheAgentHasRestarted() {
		var ui = new ManualUiDispatcher(paused: false);
		await using var host = await TestHost.StartAsync(NoopSystemNotificationChannel.Instance, ui);
		var session = await StartBackgroundAsync(host, "restarted");
		ui.Pause();

		var restart = host.InvokeCommandAsync(session.SlotId, CoreCommands.RestartAgent,
			new { stopBackgroundWork = true }, CancellationToken.None);
		await ui.WaitForPostAsync();

		Assert.False(restart.IsCompleted);
		ui.RunPending();
		Assert.True((await restart).Ok);
		Assert.False(session.Agent.Background!.BackgroundWork.Single().Running);
	}

	private static Task<HostSession> StartBackgroundAsync(TestHost host, string branch) =>
		StartBackgroundAsync(host, branch, FakeStructuredAgentProvider.BackgroundPrompt);

	private static async Task<HostSession> StartBackgroundAsync(TestHost host, string branch, string prompt) {
		Assert.True((await host.CreateSessionAsync(new NewSessionRequest { Branch = branch, Base = "main", AgentProviderId = "structured" })).Ok);
		var session = host.Session(branch);
		host.SessionEvent(session, "agent", "submit",
			new { id = "", prompt, kind = "prompt", commandName = "", attachmentIds = Array.Empty<string>() });
		Assert.True(session.Agent.Background!.BackgroundWork.Single().Running);
		return session;
	}
}
