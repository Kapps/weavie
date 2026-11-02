using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Inference;
using Weavie.Core.Sessions;
using Weavie.Core.Workflows;
using Weavie.TestSupport;
using Xunit;

namespace Weavie.Hosting.Tests;

// A session's workflow run through HostCore: the run commands, stage turns delivered to the agent, the stage block
// on every turn, the session-list run line, and a run that survives an unload.
public sealed class HostCoreWorkflowTests {
	private const string Flow = """
		id: flow
		description: gated stage, then a decided stage
		stages:
		  - id: draft
		    do: { prompt: "write the draft", outputs: { note: text } }
		    gate: approve
		  - id: polish
		    do: { prompt: "polish {{draft.note}}" }
		    next:
		      prompt: "Is it polished?"
		      choices: { yes: done, no: polish }
		""";

	private static void CommitFlow(string repo) {
		Directory.CreateDirectory(Path.Combine(repo, ".weavie", "workflows"));
		File.WriteAllText(Path.Combine(repo, ".weavie", "workflows", "flow.yaml"), Flow);
		TempGitRepo.Run(repo, "add", ".");
		TempGitRepo.Run(repo, "commit", "-m", "flow");
	}

	private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

	// Pane output publishes immediately: the host's clock is manual, so a coalescing delay would never elapse.
	private static async Task<TestHost> StartAsync(DecisionStub decisions) {
		var host = await TestHost.StartAsync(CommitFlow, _ => decisions);
		host.Settings.Set(AgentSettings.PaneCoalesceMs, JsonSerializer.SerializeToElement(0L));
		return host;
	}

	[Fact]
	public async Task RunDeliversStagesCarriesTheStageBlockAndPublishesTheRunLine() {
		var decisions = new DecisionStub("yes");
		await using var host = await StartAsync(decisions);
		var provider = Assert.IsType<FakeStructuredAgentProvider>(host.AgentProviders.RequireAvailable("structured"));
		IAgentTurnContext? turnContext = null;
		provider.CreatingSession = context => turnContext = context.TurnContext;
		Assert.True((await host.CreateSessionAsync(new NewSessionRequest { Branch = "wf", Base = "main", AgentProviderId = "structured" })).Ok);
		var session = host.Session("wf");

		Assert.True((await host.InvokeCommandAsync("wf", CoreCommands.WorkflowRun, new { workflow = "flow" }, CancellationToken.None)).Ok);
		await WaitForEchoAsync(host, session, "write the draft");
		string runId = session.Workflow.Current!.Id;
		Assert.Contains("at stage \"draft\"", turnContext!.Compose(), StringComparison.Ordinal);
		await WaitForRunLineAsync(host, session, line => line?.GetProperty("stage").GetString() == "draft");

		Assert.Contains("waits for the user's approval", session.Workflow.CompleteStage(Json("""{"note":"v1"}""")), StringComparison.Ordinal);
		await WaitForRunLineAsync(host, session, line => line?.GetProperty("state").GetString() == "approval");
		Assert.False((await host.InvokeCommandAsync("wf", CoreCommands.WorkflowRejectStage,
			new { run = "r-other", feedback = "x" }, CancellationToken.None)).Ok);
		Assert.True((await host.InvokeCommandAsync("wf", CoreCommands.WorkflowRejectStage,
			new { run = runId, feedback = "shorter please" }, CancellationToken.None)).Ok);
		await WaitForEchoAsync(host, session, "I didn't approve stage \"draft\" yet: shorter please");

		session.Workflow.CompleteStage(Json("""{"note":"v2"}"""));
		Assert.True((await host.InvokeCommandAsync("wf", CoreCommands.WorkflowApproveStage, new { run = runId }, CancellationToken.None)).Ok);
		await WaitForEchoAsync(host, session, "polish v2");

		session.Workflow.CompleteStage(Json("{}"));
		await WaitForRunLineAsync(host, session, line => line is null);
		Assert.Equal(WorkflowPhase.Done, session.Workflow.Current!.Phase);
		Assert.Equal("", turnContext.Compose());
	}

	[Fact]
	public async Task UnsureDecisionPausesTheRunAndResumeAsksAgain() {
		var decisions = new DecisionStub("unsure");
		await using var host = await StartAsync(decisions);
		Assert.True((await host.CreateSessionAsync(new NewSessionRequest { Branch = "wf", Base = "main", AgentProviderId = "structured" })).Ok);
		var session = host.Session("wf");
		var run = session.Workflow.Start("flow", Json("{}"));
		session.Workflow.CompleteStage(Json("""{"note":"v1"}"""));
		session.Workflow.Approve(run.Id);
		session.Workflow.CompleteStage(Json("{}"));

		await WaitForRunLineAsync(host, session, line => line?.GetProperty("state").GetString() == "paused");
		decisions.Choice = "yes";
		Assert.True((await host.InvokeCommandAsync("wf", CoreCommands.WorkflowResume, new { run = run.Id }, CancellationToken.None)).Ok);
		await WaitForRunLineAsync(host, session, line => line is null);
	}

	[Fact]
	public async Task NewSessionStartsItsWorkflowAsTheFirstInputAndRefusesBadOnesBeforeCreatingIt() {
		await using var host = await StartAsync(new DecisionStub("yes"));

		Assert.True((await host.CreateSessionAsync(new NewSessionRequest {
			Branch = "wf",
			Base = "main",
			AgentProviderId = "structured",
			Workflow = "flow",
		})).Ok);
		await WaitForEchoAsync(host, host.Session("wf"), "write the draft");

		var unknown = await host.CreateSessionAsync(new NewSessionRequest {
			Branch = "nope",
			Base = "main",
			AgentProviderId = "structured",
			Workflow = "missing",
		});
		Assert.False(unknown.Ok);
		var both = await host.CreateSessionAsync(new NewSessionRequest {
			Branch = "both",
			Base = "main",
			AgentProviderId = "structured",
			Workflow = "flow",
			Prompt = "hi",
		});
		Assert.False(both.Ok);
		Assert.Null(host.Core.SessionForTest("nope"));
		Assert.Null(host.Core.SessionForTest("both"));
	}

	[Fact]
	public async Task RunSurvivesAnUnloadAndReplaysItsLine() {
		await using var host = await StartAsync(new DecisionStub("yes"));
		Assert.True((await host.CreateSessionAsync(new NewSessionRequest { Branch = "wf", Base = "main", AgentProviderId = "structured" })).Ok);
		var run = host.Session("wf").Workflow.Start("flow", Json("{}"));
		host.Session("wf").Workflow.CompleteStage(Json("""{"note":"v1"}"""));

		Assert.True((await host.UnloadSessionAsync("wf")).Ok);
		Assert.True((await host.InvokeCommandAsync(host.WorkspaceSession.SlotId, SessionCommands.LoadSession,
			new { id = "wf" }, CancellationToken.None)).Ok);
		var reloaded = await WaitForSessionAsync(host, "wf");

		Assert.Equal((run.Id, WorkflowPhase.AwaitingApproval), (reloaded.Workflow.Current!.Id, reloaded.Workflow.Current.Phase));
		await WaitForRunLineAsync(host, reloaded, line => line?.GetProperty("state").GetString() == "approval");
	}

	[Theory]
	[InlineData("cancel")]
	[InlineData("fail")]
	public async Task ATurnQueuedWhileTheAgentIsBusyIsNotSentOnceTheRunNoLongerOwesIt(string retire) {
		await using var host = await StartAsync(new DecisionStub("yes"));
		Assert.True((await host.CreateSessionAsync(new NewSessionRequest { Branch = "wf", Base = "main", AgentProviderId = "structured" })).Ok);
		var session = host.Session("wf");
		var run = session.Workflow.Start("flow", Json("{}"));
		await WaitForEchoAsync(host, session, "write the draft");
		session.Workflow.CompleteStage(Json("""{"note":"v1"}"""));

		session.Status.Observe(new AgentPromptSubmitted(null, "busy"));
		session.Workflow.Approve(run.Id);
		if (retire == "cancel") session.Workflow.Cancel(run.Id);
		else session.Workflow.FailStage("stuck");
		session.Status.Observe(new AgentTurnStopped(false));

		await Task.Delay(200);
		Assert.DoesNotContain(host.Bridge.PostedEvents(session.Address, "agent", "pane"), message =>
			message.GetProperty("text").GetString()?.StartsWith("echo: polish", StringComparison.Ordinal) == true);
	}

	[Fact]
	public async Task DeletingASessionForgetsItsRunSoTheBranchCanStartAfresh() {
		await using var host = await StartAsync(new DecisionStub("yes"));
		Assert.True((await host.CreateSessionAsync(new NewSessionRequest { Branch = "wf", Base = "main", AgentProviderId = "structured" })).Ok);
		host.Session("wf").Workflow.Start("flow", Json("{}"));

		host.SelectWorkspaceSession();
		Assert.True((await host.DeleteSessionAsync("wf", force: true, classify: false)).Ok);
		var reopened = await host.CreateSessionAsync(new NewSessionRequest {
			Branch = "wf",
			Existing = true,
			AgentProviderId = "structured",
			Workflow = "flow",
		});
		Assert.True(reopened.Ok, reopened.Error);
		var recreated = await WaitForSessionAsync(host, "wf");
		Assert.Equal(("draft", WorkflowPhase.Open), (recreated.Workflow.Current!.Stage, recreated.Workflow.Current.Phase));
	}

	[Fact]
	public async Task NewSessionRunsTheWorkflowResolvedWhereItWasStarted() {
		await using var host = await StartAsync(new DecisionStub("yes"));
		File.WriteAllText(Path.Combine(host.RepoRoot, ".weavie", "workflows", "local.yaml"), """
			id: local
			description: exists only uncommitted in the source checkout
			stages:
			  - id: only
			    do: { prompt: "local stage" }
			""");

		Assert.True((await host.CreateSessionAsync(new NewSessionRequest {
			Branch = "fresh",
			Base = "main",
			AgentProviderId = "structured",
			Workflow = "local",
		})).Ok);
		var session = await WaitForSessionAsync(host, "fresh");
		Assert.False(File.Exists(Path.Combine(session.WorkspaceRoot, ".weavie", "workflows", "local.yaml")));
		await WaitForEchoAsync(host, session, "local stage");
	}

	private static async Task WaitForEchoAsync(TestHost host, HostSession session, string prompt) =>
		await Eventually(() => host.Bridge.PostedEvents(session.Address, "agent", "pane").Any(message =>
			message.GetProperty("type").GetString() == "item-completed"
			&& message.GetProperty("text").GetString()!.StartsWith($"echo: {prompt}", StringComparison.Ordinal)));

	private static async Task WaitForRunLineAsync(TestHost host, HostSession session, Func<JsonElement?, bool> matches) =>
		await Eventually(() => host.Bridge.PostedEvents(session.Address, "status", "run") is { Count: > 0 } lines
			&& matches(lines[^1].GetProperty("run") is { ValueKind: JsonValueKind.Object } run ? run : null));

	private static async Task<HostSession> WaitForSessionAsync(TestHost host, string slot) {
		await Eventually(() => host.Core.SessionForTest(slot) is not null);
		return host.Session(slot);
	}

	private static async Task Eventually(Func<bool> condition) {
		for (int i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
		Assert.True(condition());
	}

	// Answers every workflow decision with Choice.
	private sealed class DecisionStub(string choice) : IInferenceService {
		public string Choice { get; set; } = choice;

		public Task<InferenceResult<TResponse>> QueryAsync<TResponse>(
			InferenceOwner owner,
			InferenceModelCategory category,
			InferenceInput input,
			JsonTypeInfo<TResponse> responseType,
			InferenceQueryOptions options,
			CancellationToken ct) =>
			Task.FromResult<InferenceResult<TResponse>>(new InferenceSuccess<TResponse> {
				Value = JsonSerializer.Deserialize($$"""{"choice":"{{Choice}}"}""", responseType)!,
				Receipt = new InferenceReceipt {
					ProviderId = "test",
					Category = category,
					ModelId = "utility-model",
					Duration = TimeSpan.Zero,
				},
			});
	}
}
