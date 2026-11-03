using System.Text.Json;
using Xunit;

namespace Weavie.Workflows.Tests;

public sealed class WorkflowRunnerTests : IDisposable {
	private const string Flow = """
		id: flow
		description: test flow
		stages:
		  - id: a
		    run: { prompt: "do a", outputs: { note: text } }
		    then:
		      ask: "approve {{a.note}}?"
		      by: user
		      choices: { approve: b, revise: a }
		  - id: b
		    run: { prompt: "do b" }
		    then:
		      ask: "again after {{a.note}}?"
		      choices: { yes: a, no: done }
		""";

	private readonly TempDirectory _dir = new("weavie-workflow-runner");
	private readonly Port _port = new();
	private readonly Decider _decider = new();
	private readonly MemoryStore _store = new();

	public WorkflowRunnerTests() {
		_dir.WriteFile("worktree/.weavie/workflows/flow.yaml", Flow);
	}

	public void Dispose() => _dir.Dispose();

	private WorkflowRunner Runner() {
		var runner = new WorkflowRunner(
			new WorkflowCatalog(_dir.Combine("worktree"), _dir.Combine("personal")),
			_store, _decider, _port, () => 3, _dir.Combine("worktree"));
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
		Assert.Equal(run.Id, WorkflowRunDocument.Read(_store.Text)?.Id);
	}

	[Fact]
	public void CompleteStageRepliesWithErrorsOrTheNextStep() {
		using var runner = Runner();
		runner.Start("flow", Json("{}"));
		Assert.Contains("missing output \"note\"", runner.CompleteStage(Json("{}"), "s"));
		Assert.Contains("Weavie now asks the user: approve n?", runner.CompleteStage(Json("""{"note":"n"}"""), "s"));
		Assert.Contains("weavie.workflow.answer with run", runner.StageBlock());
		Assert.Contains("Never answer for the user", runner.StageBlock());
		Assert.Single(_port.Notices);
	}

	[Fact]
	public void UserCommandsTargetOnlyTheLiveRun() {
		using var runner = Runner();
		var run = runner.Start("flow", Json("{}"));
		runner.CompleteStage(Json("""{"note":"n"}"""), "s");
		Assert.Throws<WorkflowException>(() => runner.Answer("r-000000000000", "approve", ""));
		runner.Answer(run.Id, "approve", "");
		Assert.Equal("do b", _port.Kickoffs[^1]);
		runner.Cancel(run.Id);
		Assert.Throws<WorkflowException>(() => runner.CompleteStage(Json("{}"), "s"));
		Assert.Equal("", runner.StageBlock());
	}

	[Fact]
	public async Task DecisionAnswerAdvancesTheRun() {
		using var runner = Runner();
		var run = runner.Start("flow", Json("{}"));
		runner.CompleteStage(Json("""{"note":"n"}"""), "s");
		runner.Answer(run.Id, "approve", "");
		_decider.Next = new TaskCompletionSource<WorkflowDecisionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		runner.CompleteStage(Json("{}"), "s");
		Assert.Equal(WorkflowPhase.Deciding, runner.Current!.Phase);
		_decider.Next.SetResult(WorkflowDecisionResult.Chose("no"));
		await WaitFor(() => runner.Current!.Phase == WorkflowPhase.Done);
	}

	[Fact]
	public async Task LateDecisionAfterJumpIsDropped() {
		using var runner = Runner();
		var run = runner.Start("flow", Json("{}"));
		runner.CompleteStage(Json("""{"note":"n"}"""), "s");
		runner.Answer(run.Id, "approve", "");
		var late = new TaskCompletionSource<WorkflowDecisionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		_decider.Next = late;
		runner.CompleteStage(Json("{}"), "s");
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
			Assert.True(reloaded.TryClaimKickoff(WorkflowKickoff.Prompt("do a")));
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

		_store.Text = _store.Text.Replace("do a", "{{nope.x}}", StringComparison.Ordinal);
		using var reloaded = Runner();
		Assert.Equal(WorkflowPhase.Paused, reloaded.Current!.Phase);
		Assert.Contains("no longer validates", reloaded.Current.PausedReason);
	}

	[Fact]
	public void AnOwedTurnIsClaimedOnlyWhileTheRunStillOwesIt() {
		using var runner = Runner();
		var run = runner.Start("flow", Json("{}"));
		var owed = WorkflowKickoff.Prompt("do a");
		runner.FailStage("stuck");
		Assert.False(runner.TryClaimKickoff(owed));

		runner.Resume(run.Id);
		runner.Cancel(run.Id);
		Assert.False(runner.TryClaimKickoff(owed));
	}

	[Fact]
	public void AClosedRunnerRefusesEveryChange() {
		var runner = Runner();
		var run = runner.Start("flow", Json("{}"));
		runner.Dispose();
		Assert.False(runner.TryClaimKickoff(WorkflowKickoff.Prompt("do a")));
		Assert.Throws<WorkflowException>(() => runner.CompleteStage(Json("""{"note":"n"}"""), "s"));
		Assert.Throws<WorkflowException>(() => runner.Cancel(run.Id));
		Assert.Throws<WorkflowException>(() => runner.Start("flow", Json("{}")));
		Assert.Equal(WorkflowPhase.Open, runner.Current!.Phase);
	}

	[Fact]
	public void DecisionAnswersOutsideTheChoicesFail() {
		var request = new WorkflowDecisionRequest { Question = "q", Choices = ["yes", "no"], Outputs = new Dictionary<string, string>(), Summary = "" };
		Assert.Equal("yes", WorkflowDecision.Interpret(request, "yes").Choice);
		Assert.Equal(WorkflowDecision.Unsure, WorkflowDecision.Interpret(request, "unsure").Choice);
		Assert.Contains("isn't one of yes, no", WorkflowDecision.Interpret(request, "maybe").Failure);
		Assert.Equal(["yes", "no", WorkflowDecision.Unsure], WorkflowDecision.ModelChoices(request));
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

	private sealed class MemoryStore : IWorkflowRunStore {
		public string Text { get; set; } = WorkflowRunDocument.Write(null);

		public WorkflowRun? Run => WorkflowRunDocument.Read(Text);

		public string? LoadFailure => null;

		public void Save(WorkflowRun? run) => Text = WorkflowRunDocument.Write(run);
	}

	private sealed class Decider : IWorkflowDecider {
		public TaskCompletionSource<WorkflowDecisionResult> Next { get; set; } = new();

		public Task<WorkflowDecisionResult> DecideAsync(WorkflowDecisionRequest request, CancellationToken ct) => Next.Task.WaitAsync(ct);
	}
}
