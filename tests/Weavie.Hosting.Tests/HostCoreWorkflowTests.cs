using System.Text.Json;
using Weavie.Core;
using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Inference;
using Weavie.Core.Sessions;
using Weavie.Core.Workflows;
using Weavie.Core.Workspaces;
using Weavie.TestSupport;
using Weavie.Workflows;
using Xunit;

namespace Weavie.Hosting.Tests;

// A session's workflow run through HostCore: the run commands, stage turns delivered to the agent, the stage block
// on every turn, the session-list run line, and a run that survives an unload.
public sealed class HostCoreWorkflowTests {
	private const string Flow = """
		id: flow
		description: a stage the user approves, then a stage the model decides on
		stages:
		  - id: draft
		    run: { prompt: "write the draft", outputs: { note: text } }
		    then:
		      ask: "Approve the draft {{draft.note}}?"
		      by: user
		      choices: { approve: polish, revise: draft }
		  - id: polish
		    run: { prompt: "polish {{draft.note}}" }
		    then:
		      ask: "Is {{draft.note}} polished?"
		      choices: { yes: done, no: polish }
		""";

	internal static void CommitFlow(string repo) {
		Directory.CreateDirectory(Path.Combine(repo, ".weavie", "workflows"));
		File.WriteAllText(Path.Combine(repo, ".weavie", "workflows", "flow.yaml"), Flow);
		TempGitRepo.Run(repo, "add", ".");
		TempGitRepo.Run(repo, "commit", "-m", "flow");
	}

	internal static WorkflowDecisionHistory DecisionHistory(string repo) =>
		new(new LocalFileSystem(), WeaviePaths.WorkspaceDecisionHistoryFile(WorkspaceId.ForPath(repo)));

	internal static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

	// Pane output publishes immediately: the host's clock is manual, so a coalescing delay would never elapse.
	internal static Task<TestHost> StartAsync(DecisionStub decisions) => StartAsync(decisions, CommitFlow);

	internal static async Task<TestHost> StartAsync(DecisionStub decisions, Action<string> prepareRepo) {
		var host = await TestHost.StartAsync(prepareRepo, _ => decisions);
		host.Settings.Set(AgentSettings.PaneCoalesceMs, JsonSerializer.SerializeToElement(0L));
		host.Settings.Set(InferenceSettings.Enabled, JsonSerializer.SerializeToElement(true));
		return host;
	}

	[Fact]
	public async Task RunDeliversStagesCarriesTheStageBlockAndPublishesTheRunLine() {
		var decisions = new DecisionStub("yes", 0.9);
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

		Assert.Contains("Weavie now asks the user: Approve the draft v1?", session.Workflow.CompleteStage(Json("""{"note":"v1"}"""), "s"), StringComparison.Ordinal);
		await WaitForRunLineAsync(host, session, line => line?.GetProperty("state").GetString() == "needsAnswer"
			&& line.Value.GetProperty("reason").GetString() == "Approve the draft v1?");
		Assert.False((await host.InvokeCommandAsync("wf", CoreCommands.WorkflowAnswer,
			new { run = "r-other", choice = "revise", feedback = "x" }, CancellationToken.None)).Ok);
		Assert.False((await host.InvokeCommandAsync("wf", CoreCommands.WorkflowAnswer,
			new { run = runId, choice = "maybe" }, CancellationToken.None)).Ok);
		Assert.True((await host.InvokeCommandAsync("wf", CoreCommands.WorkflowAnswer,
			new { run = runId, choice = "revise", feedback = "shorter please" }, CancellationToken.None)).Ok);
		await WaitForEchoAsync(host, session, "The user answered \"revise\" to \"Approve the draft v1?\"");

		session.Workflow.CompleteStage(Json("""{"note":"v2"}"""), "s");
		Assert.True((await host.InvokeCommandAsync("wf", CoreCommands.WorkflowAnswer, new { run = runId, choice = "approve" }, CancellationToken.None)).Ok);
		await WaitForEchoAsync(host, session, "polish v2");

		session.Workflow.CompleteStage(Json("{}"), "s");
		await WaitForRunLineAsync(host, session, line => line is null);
		Assert.Equal(WorkflowPhase.Done, session.Workflow.Current!.Phase);
		Assert.Equal("", turnContext.Compose());
		Assert.Equal(1, DecisionHistory(host.RepoRoot).Summary().AgentAnswered);
	}

	[Fact]
	public async Task LowConfidenceAsksTheUserTheSameQuestion() {
		var decisions = new DecisionStub("yes", 0.79);
		await using var host = await StartAsync(decisions);
		Assert.True((await host.CreateSessionAsync(new NewSessionRequest { Branch = "wf", Base = "main", AgentProviderId = "structured" })).Ok);
		var session = host.Session("wf");
		var run = session.Workflow.Start("flow", Json("{}"));
		session.Workflow.CompleteStage(Json("""{"note":"v1"}"""), "s");
		session.Workflow.Answer(run.Id, "approve", "");
		session.Workflow.CompleteStage(Json("{}"), "s");

		await WaitForRunLineAsync(host, session, line => line?.GetProperty("state").GetString() == "needsAnswer"
			&& line.Value.GetProperty("reason").GetString() == "Is v1 polished? (the decision confidence 0.79 is below the required 0.8)");
		Assert.True((await host.InvokeCommandAsync("wf", CoreCommands.WorkflowAnswer, new { run = run.Id, choice = "yes" }, CancellationToken.None)).Ok);
		await WaitForRunLineAsync(host, session, line => line is null);
	}

	[Fact]
	public async Task NewSessionStartsItsWorkflowAsTheFirstInputAndRefusesBadOnesBeforeCreatingIt() {
		await using var host = await StartAsync(new DecisionStub("yes", 0.9));

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
		await using var host = await StartAsync(new DecisionStub("yes", 0.9));
		Assert.True((await host.CreateSessionAsync(new NewSessionRequest { Branch = "wf", Base = "main", AgentProviderId = "structured" })).Ok);
		var run = host.Session("wf").Workflow.Start("flow", Json("{}"));
		host.Session("wf").Workflow.CompleteStage(Json("""{"note":"v1"}"""), "s");

		Assert.True((await host.UnloadSessionAsync("wf")).Ok);
		Assert.True((await host.InvokeCommandAsync(host.WorkspaceSession.SlotId, SessionCommands.LoadSession,
			new { id = "wf" }, CancellationToken.None)).Ok);
		var reloaded = await WaitForSessionAsync(host, "wf");

		Assert.Equal((run.Id, WorkflowPhase.Asking), (reloaded.Workflow.Current!.Id, reloaded.Workflow.Current.Phase));
		await WaitForRunLineAsync(host, reloaded, line => line?.GetProperty("state").GetString() == "needsAnswer");
	}

	[Theory]
	[InlineData("cancel")]
	[InlineData("fail")]
	public async Task ATurnQueuedWhileTheAgentIsBusyIsNotSentOnceTheRunNoLongerOwesIt(string retire) {
		await using var host = await StartAsync(new DecisionStub("yes", 0.9));
		Assert.True((await host.CreateSessionAsync(new NewSessionRequest { Branch = "wf", Base = "main", AgentProviderId = "structured" })).Ok);
		var session = host.Session("wf");
		var run = session.Workflow.Start("flow", Json("{}"));
		await WaitForEchoAsync(host, session, "write the draft");
		session.Workflow.CompleteStage(Json("""{"note":"v1"}"""), "s");

		session.Status.Observe(new AgentPromptSubmitted(null, "busy"));
		session.Workflow.Answer(run.Id, "approve", "");
		if (retire == "cancel") session.Workflow.Cancel(run.Id);
		else session.Workflow.FailStage("stuck");
		session.Status.Observe(new AgentTurnStopped(false));

		await Task.Delay(200);
		Assert.DoesNotContain(host.Bridge.PostedEvents(session.Address, "agent", "pane"), message =>
			message.GetProperty("text").GetString()?.StartsWith("echo: polish", StringComparison.Ordinal) == true);
	}

	[Fact]
	public async Task DeletingASessionForgetsItsRunSoTheBranchCanStartAfresh() {
		await using var host = await StartAsync(new DecisionStub("yes", 0.9));
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
		await using var host = await StartAsync(new DecisionStub("yes", 0.9));
		File.WriteAllText(Path.Combine(host.RepoRoot, ".weavie", "workflows", "local.yaml"), """
			id: local
			description: exists only uncommitted in the source checkout
			stages:
			  - id: only
			    run: { prompt: "local stage" }
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

	internal static async Task WaitForRunLineAsync(TestHost host, HostSession session, Func<JsonElement?, bool> matches) =>
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

	// Answers every workflow decision with Choice at the given probability, and every other label with 0.
	internal sealed class DecisionStub(string choice, double confidence) : IInferenceService {
		public string Choice { get; set; } = choice;

		public Task<InferenceResult<TResponse>> QueryAsync<TResponse>(
			InferenceOwner owner,
			InferenceModelCategory category,
			InferenceInput input,
			InferenceResponseType<TResponse> responseType,
			InferenceQueryOptions options,
			CancellationToken ct) {
			using var schema = JsonDocument.Parse(responseType.SchemaJson);
			var labels = schema.RootElement.GetProperty("required").EnumerateArray().Select(label => label.GetString()!);
			string reply = "{" + string.Join(",", labels.Select(label =>
				FormattableString.Invariant($"\"{label}\":{(label == Choice ? confidence : 0)}"))) + "}";
			return Task.FromResult<InferenceResult<TResponse>>(new InferenceSuccess<TResponse> {
				Value = JsonSerializer.Deserialize(reply, responseType.TypeInfo)!,
				Receipt = new InferenceReceipt {
					ProviderId = "test",
					ModelId = "utility-model",
					Duration = TimeSpan.Zero,
				},
			});
		}
	}
}
