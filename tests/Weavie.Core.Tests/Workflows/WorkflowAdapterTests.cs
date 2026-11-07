using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;
using Weavie.Core.Workflows;
using Weavie.Workflows;
using Xunit;

namespace Weavie.Core.Tests.Workflows;

public sealed class WorkflowAdapterTests : IDisposable {
	private const string Flow = """
		id: flow
		description: test flow
		stages:
		  - id: a
		    run: { prompt: "do a" }
		""";

	private readonly TempDirectory _dir = new("weavie-workflow-adapters");

	public WorkflowAdapterTests() {
		_dir.WriteFile("worktree/.weavie/workflows/flow.yaml", Flow);
	}

	public void Dispose() => _dir.Dispose();

	private string StorePath => _dir.Combine("run.json");

	private WorkflowCatalog Catalog() => new(_dir.Combine("worktree"), _dir.Combine("personal"), WeaviePaths.RepositoryWorkflowsDir(_dir.Combine("worktree")));

	private WorkflowRunner Runner() {
		var runner = new WorkflowRunner(
			Catalog(), new WorkflowRunStore(new LocalFileSystem(), StorePath), new NoDecider(), new NoPort(), () => 3, () => true, _dir.Combine("worktree"), WeavieWorkflows.Vocabulary);
		runner.Activate();
		return runner;
	}

	private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

	[Fact]
	public void StorePersistsTheRunAcrossInstances() {
		using var runner = Runner();
		var run = runner.Start("flow", Json("{}"));
		Assert.Equal(run.Id, new WorkflowRunStore(new LocalFileSystem(), StorePath).Run?.Id);
	}

	[Fact]
	public void UnreadableStoreIsSetAsideAndReported() {
		File.WriteAllText(StorePath, "{not json");
		using var runner = Runner();
		Assert.Null(runner.Current);
		Assert.Contains("couldn't be read", runner.Problem);
		Assert.True(File.Exists(StorePath + ".bad"));
		runner.Start("flow", Json("{}"));
		Assert.Null(runner.Problem);
	}

	[Fact]
	public void SessionRegistryRoutesToItsRunnerAndErrorsWithoutARun() {
		using var runner = Runner();
		var registry = new SessionWorkflowRegistry(Catalog(), runner);
		Assert.Throws<WorkflowException>(() => registry.CompleteStage(Json("{}"), "s"));
		runner.Start("flow", Json("{}"));
		registry.FailStage("stuck");
		Assert.Equal(WorkflowPhase.Paused, runner.Current!.Phase);
	}

	[Fact]
	public void SummaryCarriesThePauseReasonAndTheQuestionAwaitingTheUser() {
		using var runner = Runner();
		var run = runner.Start("flow", Json("{}"));
		var asking = run with {
			Phase = WorkflowPhase.Asking,
			Asked = new WorkflowAsked { Question = "ship it?", Choices = ["yes", "no"], Reason = "the decision model was unsure" },
		};
		Assert.Equal("ship it? (the decision model was unsure)", WorkflowRunSummary.From(asking, SessionStatus.Idle)!.Reason);
		var paused = run with { Phase = WorkflowPhase.Paused, PausedReason = "stuck" };
		Assert.Equal("stuck", WorkflowRunSummary.From(paused, SessionStatus.Idle)!.Reason);
	}

	[Theory]
	[InlineData(WorkflowPhase.Open, SessionStatus.Working, WorkflowRunState.Working)]
	[InlineData(WorkflowPhase.Open, SessionStatus.Starting, WorkflowRunState.Working)]
	[InlineData(WorkflowPhase.Open, SessionStatus.Idle, WorkflowRunState.WaitingOnYou)]
	[InlineData(WorkflowPhase.Open, SessionStatus.NeedsInput, WorkflowRunState.WaitingOnYou)]
	[InlineData(WorkflowPhase.Open, SessionStatus.Waiting, WorkflowRunState.Watching)]
	[InlineData(WorkflowPhase.Open, SessionStatus.Error, WorkflowRunState.Stalled)]
	[InlineData(WorkflowPhase.Asking, SessionStatus.Idle, WorkflowRunState.NeedsAnswer)]
	[InlineData(WorkflowPhase.Deciding, SessionStatus.Idle, WorkflowRunState.Deciding)]
	[InlineData(WorkflowPhase.Paused, SessionStatus.Working, WorkflowRunState.Paused)]
	public void SummaryCombinesPhaseAndSessionStatus(WorkflowPhase phase, SessionStatus status, WorkflowRunState state) {
		using var runner = Runner();
		var run = runner.Start("flow", Json("{}")) with {
			Phase = phase,
			Asked = new WorkflowAsked { Question = "q", Choices = ["a", "b"], Reason = "" },
		};
		Assert.Equal(state, WorkflowRunSummary.From(run, status)!.State);
		Assert.Null(WorkflowRunSummary.From(run with { Phase = WorkflowPhase.Done }, status));
	}

	[Fact]
	public void KickoffRoundTripsThroughAnAgentTurn() {
		var skill = new WorkflowKickoff { Text = "/spec x", Command = "spec" };
		Assert.Equal(skill, skill.Submission().Kickoff());
		Assert.Equal(AgentTurnSubmissionKind.ProviderCommand, skill.Submission().Kind);
		Assert.Equal(AgentTurnSubmissionKind.Prompt, WorkflowKickoff.Prompt("go").Submission().Kind);
	}

	private sealed class NoDecider : IWorkflowDecider {
		public Task<WorkflowDecisionResult> DecideAsync(WorkflowDecisionRequest request, CancellationToken ct) =>
			throw new InvalidOperationException("No decision expected.");
	}

	private sealed class NoPort : IWorkflowRunPort {
		public void Kickoff(WorkflowKickoff turn) {
		}

		public void Notify(string message) {
		}
	}
}
