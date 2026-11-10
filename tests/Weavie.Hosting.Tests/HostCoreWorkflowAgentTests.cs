using System.Text.Json;
using Weavie.AgentClientProtocol;
using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;
using Weavie.TestSupport;
using Weavie.Workflows;
using Xunit;
using static Weavie.Hosting.Tests.HostCoreWorkflowTests;

namespace Weavie.Hosting.Tests;

// A stage's agent through HostCore: its model takes effect before the stage's turn, a stage naming none and the run's
// end return the session to its own model, pane picks follow the hold, and a session that can't run it refuses.
public sealed class HostCoreWorkflowAgentTests {
	private static void CommitAgents(string repo) {
		CommitFlow(repo);
		File.WriteAllText(Path.Combine(repo, ".weavie", "workflows", "agents.yaml"), """
			id: agents
			description: build on the next model, then ship on the session's own
			agents:
			  builder: { provider: structured, model: GPT Next }
			  gone: { provider: structured, model: GPT Gone }
			stages:
			  - id: build
			    run: { prompt: "build it", agent: builder }
			  - id: ship
			    run: { prompt: "ship it" }
			  - id: broken
			    run: { prompt: "never sent", agent: gone }
			""");
		File.WriteAllText(Path.Combine(repo, ".weavie", "workflows", "foreign.yaml"), """
			id: foreign
			description: runs only on another provider
			agents: { reviewer: { provider: other-acp } }
			stages:
			  - id: review
			    run: { prompt: "review it", agent: reviewer }
			""");
		TempGitRepo.Run(repo, "add", ".");
		TempGitRepo.Run(repo, "commit", "-m", "agents");
	}

	private static async Task<(TestHost Host, HostSession Session, FakeStructuredAgentProvider Provider)> StartAsync() {
		var host = await HostCoreWorkflowTests.StartAsync(new DecisionStub("yes", 0.9), CommitAgents);
		Assert.True((await host.CreateSessionAsync(new NewSessionRequest { Branch = "wf", Base = "main", AgentProviderId = "structured" })).Ok);
		return (host, host.Session("wf"), Assert.IsType<FakeStructuredAgentProvider>(host.AgentProviders.RequireAvailable("structured")));
	}

	private static IEnumerable<JsonElement> Pane(TestHost host, HostSession session) => host.Bridge.PostedEvents(session.Address, "agent", "pane");

	private static Task WaitForNoticeAsync(TestHost host, HostSession session, string text) =>
		Eventually(() => Pane(host, session).Any(m => m.GetProperty("type").GetString() == "notice" && m.GetProperty("text").GetString() == text));

	private static string Model(HostSession session) => session.Agent.Controls!.ControlState.Axes.Single(a => a.Id == "model").Value;

	[Fact]
	public async Task TheStagesModelTakesEffectBeforeItsTurnAndAStageNamingNoneReturnsToTheSessionsOwn() {
		var (host, session, provider) = await StartAsync();
		await using var _ = host;

		Assert.True((await host.InvokeCommandAsync("wf", CoreCommands.WorkflowRun, new { workflow = "agents" }, CancellationToken.None)).Ok);
		await WaitForEchoAsync(host, session, "build it");
		Assert.Equal(["apply model=GPT Next"], provider.ControlChanges);
		await WaitForNoticeAsync(host, session, "Workflow: build runs on GPT Next");
		await WaitForRunLineAsync(host, session, line => line?.GetProperty("agent").GetString() == "builder");
		Assert.Equal("GPT Test", session.Workflow.Current!.Hold!.Own["model"]);

		session.Status.Observe(new AgentPromptSubmitted(null, "completing"));
		session.Workflow.CompleteStage(Json("{}"), "built");
		await WaitForRunLineAsync(host, session, line => line?.GetProperty("stage").GetString() == "ship");
		Assert.Equal("", host.Bridge.PostedEvents(session.Address, "status", "run")[^1].GetProperty("run").GetProperty("agent").GetString());
		session.Status.Observe(new AgentTurnStopped(false));
		await WaitForEchoAsync(host, session, "ship it");
		Assert.Equal(["apply model=GPT Next", "apply model=GPT Test"], provider.ControlChanges);
		await WaitForNoticeAsync(host, session, "Workflow: ship runs on GPT Test");
		Assert.Null(session.Workflow.Current!.Hold);
		Assert.All(
			host.Bridge.PostedEvents(session.Address, "status", "run").Select(line => line.GetProperty("run")).Where(run => run.ValueKind == JsonValueKind.Object),
			run => Assert.Contains(run.GetProperty("agent").GetString(), run.GetProperty("stage").GetString() == "build" ? ["builder", ""] : new[] { "" }));
	}

	[Fact]
	public async Task AModelTheAgentRefusesReturnsTheConversationAndPausesWithoutSendingTheTurn() {
		var (host, session, _) = await StartAsync();
		await using var _ = host;
		var run = session.Workflow.Start("agents", Json("{}"));
		await WaitForEchoAsync(host, session, "build it");

		session.Workflow.Jump(run.Id, "broken");

		await Eventually(() => session.Workflow.Current!.Phase == WorkflowPhase.Paused);
		Assert.Equal("GPT Gone is gone.", session.Workflow.Current!.PausedReason);
		await WaitForNoticeAsync(host, session, "Workflow: broken didn't open, back on GPT Test");
		Assert.Equal(("GPT Test", null), (Model(session), session.Workflow.Current.Hold));
		Assert.DoesNotContain(Pane(host, session), m => m.GetProperty("text").GetString() == "echo: never sent");
	}

	[Fact]
	public async Task AStageThatCantOpenReturnsTheConversationAndPausesWithItsOwnReason() {
		using var state = new TempDirectory("weavie-workflow-open");
		var (host, session, _) = await StartAsync();
		await using var _ = host;
		host.AgentProviders.Register(new AcpAgentProvider(
			new AcpAgentDefinition {
				Id = "effort-acp",
				Name = "Effort ACP",
				Command = AcpAgentSessionFixture.ExecutablePath("tools", "Weavie.FakeAcp", "weavie-fake-acp"),
				Arguments = [],
				Environment = new Dictionary<string, string> { ["WEAVIE_FAKE_ACP_MODE"] = "model-effort", ["WEAVIE_ROOT"] = state.Path },
			},
			host.AcpSessions, new AcpControlStore(new LocalFileSystem(), state.Combine("acp-controls.json")), _ => { }));
		Assert.True((await host.CreateSessionAsync(new NewSessionRequest { Branch = "effort", Base = "main", AgentProviderId = "effort-acp" })).Ok);
		var effort = host.Session("effort");
		await Eventually(() => effort.Agent.Controls!.ControlState.Ready);
		File.WriteAllText(Path.Combine(effort.WorkspaceRoot, ".weavie", "workflows", "effort-on-alpha.yaml"), """
			id: effort-on-alpha
			description: effort exists only on beta
			agents: { planner: { provider: effort-acp, model: alpha, options: { effort: high } } }
			stages:
			  - id: plan
			    run: { prompt: "never sent", agent: planner }
			""");
		await effort.SetAgentControlAsync("model", "beta");
		await Eventually(() => Model(effort) == "beta");

		var run = effort.Workflow.Start("effort-on-alpha", Json("{}"));

		await Eventually(() => effort.Workflow.Current!.Phase == WorkflowPhase.Paused);
		const string reason = "Stage \"plan\" sets option \"effort\", which the agent doesn't advertise; it advertises fast.";
		Assert.Equal(reason, effort.Workflow.Current!.PausedReason);
		Assert.Null(effort.Workflow.Current.Hold);
		await WaitForNoticeAsync(host, effort, "Workflow: plan didn't open, back on Beta");
		Assert.Equal("beta", Model(effort));
		Assert.Contains(host.Bridge.PostedEvents(effort.Address, "notifications", "show"), m => m.GetProperty("message").GetString() == $"Workflow effort-on-alpha paused: {reason}");
		Assert.DoesNotContain(Pane(host, effort), m => m.GetProperty("text").GetString() == "never sent");

		effort.Workflow.Resume(run.Id);
		await Eventually(() => Pane(host, effort).Count(m => m.GetProperty("text").GetString() == "Workflow: plan didn't open, back on Beta") == 2);
		Assert.Equal((WorkflowPhase.Paused, reason), (effort.Workflow.Current!.Phase, effort.Workflow.Current.PausedReason));
	}

	[Fact]
	public async Task CancellingTheRunReturnsTheSessionToItsOwnModel() {
		var (host, session, provider) = await StartAsync();
		await using var _ = host;
		var run = session.Workflow.Start("agents", Json("{}"));
		await WaitForNoticeAsync(host, session, "Workflow: build runs on GPT Next");

		session.Workflow.Cancel(run.Id);

		await WaitForNoticeAsync(host, session, "Workflow: agents ended, back on GPT Test");
		Assert.Equal("GPT Test", Model(session));
		await Eventually(() => session.Workflow.Current!.Hold is null);
		Assert.DoesNotContain(provider.ControlChanges, change => change.StartsWith("set ", StringComparison.Ordinal));
	}

	[Fact]
	public async Task APickChangesTheHeldStageOnlyAndIsTheSessionsOwnOtherwise() {
		var (host, session, provider) = await StartAsync();
		await using var _ = host;
		session.Workflow.Start("agents", Json("{}"));
		await WaitForNoticeAsync(host, session, "Workflow: build runs on GPT Next");

		await session.SetAgentControlAsync("model", "GPT Test");
		Assert.Equal("GPT Test", session.Workflow.Current!.Hold!.Held["model"]);
		Assert.Equal("apply model=GPT Test", provider.ControlChanges.Last());

		session.Status.Observe(new AgentPromptSubmitted(null, "busy"));
		session.Workflow.CompleteStage(Json("{}"), "built");
		await session.SetAgentControlAsync("model", "GPT Next");
		Assert.Equal("set model=GPT Next", provider.ControlChanges.Last());
		Assert.Equal("GPT Next", session.Workflow.Current!.Hold!.Own["model"]);

		session.Status.Observe(new AgentTurnStopped(false));
		await WaitForEchoAsync(host, session, "ship it");
		Assert.Equal(("GPT Next", null), (Model(session), session.Workflow.Current!.Hold));
		await session.SetAgentControlAsync("model", "GPT Test");
		Assert.Equal("set model=GPT Test", provider.ControlChanges.Last());
	}

	[Fact]
	public async Task ASessionThatCantRunTheWorkflowRefusesItBeforeAnyRunOrWorktreeExists() {
		var (host, session, _) = await StartAsync();
		await using var _ = host;

		var foreign = await host.InvokeCommandAsync("wf", CoreCommands.WorkflowRun, new { workflow = "foreign" }, CancellationToken.None);
		var terminal = await host.InvokeCommandAsync(host.WorkspaceSession.SlotId, CoreCommands.WorkflowRun, new { workflow = "agents" }, CancellationToken.None);
		var created = await host.CreateSessionAsync(new NewSessionRequest {
			Branch = "foreign",
			Base = "main",
			AgentProviderId = "structured",
			Workflow = "foreign",
		});

		Assert.Contains("runs only in other-acp sessions; this session runs structured", foreign.Error, StringComparison.Ordinal);
		Assert.Null(session.Workflow.Current);
		Assert.Equal("claude", host.WorkspaceSession.Agent.Provider.Id);
		Assert.Contains("runs in a terminal", terminal.Error, StringComparison.Ordinal);
		Assert.Null(host.WorkspaceSession.Workflow.Current);
		Assert.False(created.Ok);
		Assert.Contains("runs only in other-acp sessions", created.Error, StringComparison.Ordinal);
		Assert.Null(host.Core.SessionForTest("foreign"));
	}
}
