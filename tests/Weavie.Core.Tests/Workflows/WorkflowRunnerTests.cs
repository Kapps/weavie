using System.Text.Json;
using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;
using Weavie.Core.Workflows;
using Weavie.TestSupport;
using Xunit;

namespace Weavie.Core.Tests.Workflows;

public sealed class WorkflowRunnerTests : IDisposable {
	private const string Flow = """
		id: flow
		description: test flow
		stages:
		  - id: a
		    do: { prompt: "do a", outputs: { note: text } }
		    gate: approve
		  - id: b
		    do: { prompt: "do b" }
		    next:
		      prompt: "again?"
		      choices: { yes: a, no: done }
		""";

	private readonly TempDirectory _dir = new("weavie-workflow-runner");
	private readonly Port _port = new();
	private readonly Decider _decider = new();

	public WorkflowRunnerTests() {
		_dir.WriteFile("worktree/.weavie/workflows/flow.yaml", Flow);
	}

	public void Dispose() => _dir.Dispose();

	private string StorePath => _dir.Combine("run.json");

	private WorkflowRunner Runner() {
		var runner = new WorkflowRunner(
			new WorkflowCatalog(_dir.Combine("worktree"), _dir.Combine("personal")),
			new WorkflowRunStore(new LocalFileSystem(), StorePath), _decider, _port, () => 3, _dir.Combine("worktree"));
		runner.Activate();
		return runner;
	}

	private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

	[Fact]
	public void StartKicksOffPersistsAndRefusesASecondLiveRun() {
		using var runner = Runner();
		var run = runner.Start("flow", Json("{}"));
		Assert.Equal(["do a"], _port.Kickoffs);
		Assert.Matches("^r-[0-9a-f]{12}$", run.Id);
		Assert.Contains("completeStage with outputs \"note\" (text)", runner.StageBlock());
		Assert.Throws<WorkflowException>(() => runner.Start("flow", Json("{}")));
		Assert.Equal(run.Id, new WorkflowRunStore(new LocalFileSystem(), StorePath).Run?.Id);
	}

	[Fact]
	public void CompleteStageRepliesWithErrorsOrTheNextStep() {
		using var runner = Runner();
		runner.Start("flow", Json("{}"));
		Assert.Contains("missing output \"note\"", runner.CompleteStage(Json("{}")));
		Assert.Contains("approval", runner.CompleteStage(Json("""{"note":"n"}""")));
		Assert.Contains("approveStage with run", runner.StageBlock());
		Assert.Single(_port.Notices);
	}

	[Fact]
	public void UserCommandsTargetOnlyTheLiveRun() {
		using var runner = Runner();
		var run = runner.Start("flow", Json("{}"));
		runner.CompleteStage(Json("""{"note":"n"}"""));
		Assert.Throws<WorkflowException>(() => runner.Approve("r-000000000000"));
		runner.Approve(run.Id);
		Assert.Equal("do b", _port.Kickoffs[^1]);
		runner.Cancel(run.Id);
		Assert.Throws<WorkflowException>(() => runner.CompleteStage(Json("{}")));
		Assert.Equal("", runner.StageBlock());
	}

	[Fact]
	public async Task DecisionAnswerAdvancesTheRun() {
		using var runner = Runner();
		var run = runner.Start("flow", Json("{}"));
		runner.CompleteStage(Json("""{"note":"n"}"""));
		runner.Approve(run.Id);
		_decider.Next = new TaskCompletionSource<WorkflowDecisionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		runner.CompleteStage(Json("{}"));
		Assert.Equal(WorkflowPhase.Deciding, runner.Current!.Phase);
		_decider.Next.SetResult(WorkflowDecisionResult.Chose("no"));
		await WaitFor(() => runner.Current!.Phase == WorkflowPhase.Done);
	}

	[Fact]
	public async Task LateDecisionAfterJumpIsDropped() {
		using var runner = Runner();
		var run = runner.Start("flow", Json("{}"));
		runner.CompleteStage(Json("""{"note":"n"}"""));
		runner.Approve(run.Id);
		var late = new TaskCompletionSource<WorkflowDecisionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		_decider.Next = late;
		runner.CompleteStage(Json("{}"));
		runner.Jump(run.Id, "b");
		late.SetResult(WorkflowDecisionResult.Chose("yes"));
		await Task.Delay(100);
		Assert.Equal(("b", WorkflowPhase.Open), (runner.Current!.Stage, runner.Current.Phase));
	}

	[Fact]
	public void ReloadRedeliversAnUndeliveredKickoffButNotADeliveredOne() {
		using (var runner = Runner()) {
			runner.Start("flow", Json("{}"));
		}

		_port.Kickoffs.Clear();
		using (var reloaded = Runner()) {
			Assert.Equal(["do a"], _port.Kickoffs);
			reloaded.KickoffDelivered(WorkflowKickoff.Prompt("do a"));
		}

		_port.Kickoffs.Clear();
		using var again = Runner();
		Assert.Empty(_port.Kickoffs);
		Assert.Equal(WorkflowPhase.Open, again.Current!.Phase);
	}

	[Fact]
	public void ReloadedRunWhoseDefinitionNoLongerValidatesPausesLoudly() {
		using (var runner = Runner()) {
			runner.Start("flow", Json("{}"));
		}

		string text = File.ReadAllText(StorePath).Replace("do a", "{{nope.x}}", StringComparison.Ordinal);
		File.WriteAllText(StorePath, text);
		using var reloaded = Runner();
		Assert.Equal(WorkflowPhase.Paused, reloaded.Current!.Phase);
		Assert.Contains("no longer validates", _port.Notices[^1]);
	}

	[Fact]
	public void UnreadableStoreIsSetAsideAndReported() {
		File.WriteAllText(StorePath, "{not json");
		using var runner = Runner();
		Assert.Null(runner.Current);
		Assert.Contains("couldn't be read", _port.Notices[^1]);
		Assert.True(File.Exists(StorePath + ".bad"));
	}

	[Fact]
	public void SessionRegistryRoutesToItsRunnerAndErrorsWithoutARun() {
		using var runner = Runner();
		var registry = new SessionWorkflowRegistry(new WorkflowCatalog(_dir.Combine("worktree"), _dir.Combine("personal")), runner);
		Assert.Throws<WorkflowException>(() => registry.CompleteStage(Json("{}")));
		runner.Start("flow", Json("{}"));
		registry.FailStage("stuck");
		Assert.Equal(WorkflowPhase.Paused, runner.Current!.Phase);
	}

	[Theory]
	[InlineData(WorkflowPhase.Open, SessionStatus.Working, WorkflowRunState.Working)]
	[InlineData(WorkflowPhase.Open, SessionStatus.Starting, WorkflowRunState.Working)]
	[InlineData(WorkflowPhase.Open, SessionStatus.Idle, WorkflowRunState.WaitingOnYou)]
	[InlineData(WorkflowPhase.Open, SessionStatus.NeedsInput, WorkflowRunState.WaitingOnYou)]
	[InlineData(WorkflowPhase.Open, SessionStatus.Waiting, WorkflowRunState.Watching)]
	[InlineData(WorkflowPhase.Open, SessionStatus.Error, WorkflowRunState.Stalled)]
	[InlineData(WorkflowPhase.AwaitingApproval, SessionStatus.Idle, WorkflowRunState.Approval)]
	[InlineData(WorkflowPhase.Deciding, SessionStatus.Idle, WorkflowRunState.Deciding)]
	[InlineData(WorkflowPhase.Paused, SessionStatus.Working, WorkflowRunState.Paused)]
	public void SummaryCombinesPhaseAndSessionStatus(WorkflowPhase phase, SessionStatus status, WorkflowRunState state) {
		using var runner = Runner();
		var run = runner.Start("flow", Json("{}")) with { Phase = phase };
		Assert.Equal(state, WorkflowRunSummary.From(run, status)!.State);
		Assert.Null(WorkflowRunSummary.From(run with { Phase = WorkflowPhase.Done }, status));
	}

	[Fact]
	public void DecisionAnswersOutsideTheChoicesFail() {
		var request = new WorkflowDecisionRequest { Question = "q", Choices = ["yes", "no"], Outputs = new Dictionary<string, string>() };
		Assert.Equal("yes", WorkflowDecision.Interpret(request, "yes").Choice);
		Assert.Equal(WorkflowDecision.Unsure, WorkflowDecision.Interpret(request, "unsure").Choice);
		Assert.Contains("isn't one of yes, no", WorkflowDecision.Interpret(request, "maybe").Failure);
		Assert.Contains("\"unsure\"", WorkflowDecision.BuildPrompt(request));
	}

	private static async Task WaitFor(Func<bool> condition) {
		for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
		Assert.True(condition());
	}

	private sealed class Port : IWorkflowRunPort {
		public List<string> Kickoffs { get; } = [];

		public List<string> Notices { get; } = [];

		public void Kickoff(WorkflowKickoff turn) => Kickoffs.Add(turn.Text);

		public void Notify(string message) => Notices.Add(message);
	}

	private sealed class Decider : IWorkflowDecider {
		public TaskCompletionSource<WorkflowDecisionResult> Next { get; set; } = new();

		public Task<WorkflowDecisionResult> DecideAsync(WorkflowDecisionRequest request, CancellationToken ct) => Next.Task.WaitAsync(ct);
	}
}
