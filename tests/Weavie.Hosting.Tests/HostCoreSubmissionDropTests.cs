using Weavie.Core.Agents;
using Weavie.Core.Sessions;
using Weavie.TestSupport;
using Xunit;
using static Weavie.Hosting.Tests.HostCoreWorkflowTests;

namespace Weavie.Hosting.Tests;

/// <summary>A queued prompt is named on the queue feed, and reported when the agent discards it undelivered.</summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreSubmissionDropTests {
	[Fact]
	public async Task RestartingTheAgentReportsTheQueuedPromptItDropped() {
		using var state = new TempDirectory("weavie-dropped-submission");
		await using var host = await TestHost.StartAsync();
		host.RegisterFakeAcp("fake-acp", "no-steering", state.Path);
		Assert.True((await host.CreateSessionAsync(new NewSessionRequest { Branch = "dropped", Base = "main", AgentProviderId = "fake-acp" })).Ok);
		var session = host.Session("dropped");
		await Eventually(() => session.Agent.Controls!.ControlState.Ready);
		var agent = session.Agent.Structured!;
		agent.Submit(AgentTurnSubmission.Prompt("hold"));
		await Eventually(() => File.Exists(Path.Combine(session.WorkspaceRoot, "hold-started")));
		var queued = AgentTurnSubmission.Prompt("why does this retry twice?");
		agent.Submit(queued);
		await Eventually(() => host.Bridge.PostedEvents(session.Address, "agent", "queue").Any(queue =>
			queue.GetProperty("queued").EnumerateArray().Any(waiting => waiting.GetProperty("id").GetString() == queued.Id)));

		agent.Restart();

		await Eventually(() => host.Bridge.PostedEvents(session.Address, "agent", "submissionState").Any(fate =>
			fate.GetProperty("id").GetString() == queued.Id && fate.GetProperty("status").GetString() == "dropped"));
	}
}
