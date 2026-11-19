using Weavie.Core.Agents;
using Weavie.Core.Sessions;
using Weavie.TestSupport;
using Xunit;
using static Weavie.Hosting.Tests.HostCoreWorkflowTests;

namespace Weavie.Hosting.Tests;

/// <summary>A queued prompt is named on the queue feed, and an accepted prompt the agent discards undelivered is reported.</summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreSubmissionDropTests {
	[Fact]
	public async Task RestartingTheAgentReportsTheQueuedPromptItDropped() {
		using var state = new TempDirectory("weavie-dropped-queued");
		await using var host = await TestHost.StartAsync();
		var session = await HoldTurnAsync(host, state, "no-steering");
		var queued = AgentTurnSubmission.Prompt("why does this retry twice?");
		session.Agent.Structured!.Submit(queued);
		await Eventually(() => host.Bridge.PostedEvents(session.Address, "agent", "queue").Any(queue =>
			queue.GetProperty("queued").EnumerateArray().Any(waiting => waiting.GetProperty("id").GetString() == queued.Id)));

		session.Agent.Structured.Restart();

		await Eventually(() => Dropped(host, session, queued));
	}

	[Fact]
	public async Task RestartingTheAgentReportsTheSteerItHadNotAnswered() {
		using var state = new TempDirectory("weavie-dropped-steer");
		await using var host = await TestHost.StartAsync();
		var session = await HoldTurnAsync(host, state, "");
		var steer = AgentTurnSubmission.Prompt("held-steering");
		session.Agent.Structured!.Submit(steer);
		await Eventually(() => File.Exists(Path.Combine(session.WorkspaceRoot, "steering-started")));

		session.Agent.Structured.Restart();

		await Eventually(() => Dropped(host, session, steer));
	}

	[Fact]
	public async Task APromptWhoseDeliveryFailsBeforeItIsJournaledIsReportedDropped() {
		using var state = new TempDirectory("weavie-dropped-unjournaled");
		await using var host = await TestHost.StartAsync();
		var session = await StartAsync(host, state, "");
		var lost = AgentTurnSubmission.Prompt("look at this") with {
			Attachments = [new AgentInputAttachment.Image { Id = "gone", Path = state.Combine("gone.png"), Mime = "image/png" }],
		};

		session.Agent.Structured!.Submit(lost);

		await Eventually(() => Dropped(host, session, lost));
	}

	private static async Task<HostSession> StartAsync(TestHost host, TempDirectory state, string mode) {
		host.RegisterFakeAcp("fake-acp", mode, state.Path);
		Assert.True((await host.CreateSessionAsync(new NewSessionRequest { Branch = "dropped", Base = "main", AgentProviderId = "fake-acp" })).Ok);
		var session = host.Session("dropped");
		await Eventually(() => session.Agent.Controls!.ControlState.Ready);
		return session;
	}

	private static async Task<HostSession> HoldTurnAsync(TestHost host, TempDirectory state, string mode) {
		var session = await StartAsync(host, state, mode);
		session.Agent.Structured!.Submit(AgentTurnSubmission.Prompt("hold"));
		await Eventually(() => File.Exists(Path.Combine(session.WorkspaceRoot, "hold-started")));
		return session;
	}

	private static bool Dropped(TestHost host, HostSession session, AgentTurnSubmission submission) =>
		host.Bridge.PostedEvents(session.Address, "agent", "submissionState").Any(fate =>
			fate.GetProperty("id").GetString() == submission.Id && fate.GetProperty("status").GetString() == "dropped");
}
