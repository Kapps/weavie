using Xunit;

namespace Weavie.Workflows.Tests;

public sealed partial class WorkflowRunnerTests {
	[Theory]
	[InlineData(WorkflowPhase.Open)]
	[InlineData(WorkflowPhase.Asking)]
	[InlineData(WorkflowPhase.Deciding)]
	public async Task EndingTheCurrentConversationPausesItsWorkOrDecisionAndPreservesResumeState(WorkflowPhase phase) {
		string flow = Flow.Replace("run: { prompt: \"do a\"", "run: { context: fresh, prompt: \"do a\"", StringComparison.Ordinal);
		if (phase == WorkflowPhase.Deciding) flow = flow.Replace("by: user", "by: model", StringComparison.Ordinal);
		_dir.WriteFile("worktree/.weavie/workflows/flow.yaml", flow);
		using var runner = Runner();
		var started = runner.Start("flow", Json("{}"));
		var execution = runner.Execution!;
		if (phase != WorkflowPhase.Open) {
			var call = execution.Capture();
			call.Complete(Json("{\"note\":\"evidence\"}"), "Work completed");
		}
		var before = runner.Current!;
		Assert.Equal(phase, before.Phase);
		var decisionToken = phase == WorkflowPhase.Deciding ? await _decider.Started.Reader.ReadAsync() : CancellationToken.None;
		execution.ConversationEnded("Provider process exited");
		Assert.Equal(WorkflowPhase.Paused, runner.Current!.Phase);
		Assert.Same(execution, runner.Execution);
		Assert.Same(before.Outputs, runner.Current.Outputs);
		Assert.Equal(before.Summary, runner.Current.Summary);
		Assert.Equal(before.Unreported, runner.Current.Unreported);
		Assert.Equal(before.Decision, runner.Current.Decision);
		Assert.Equal(before.Asked, runner.Current.Asked);
		Assert.Contains("Provider process exited", runner.Current.PausedReason);
		Assert.Null(runner.Current.Kickoff);
		if (phase == WorkflowPhase.Deciding) Assert.True(decisionToken.IsCancellationRequested);
		execution.ConversationEnded("Later closure notification");
		Assert.Contains("Provider process exited", runner.Current.PausedReason);
		_decider.Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
		runner.Resume(started.Id);
		Assert.Same(execution, runner.Execution);
		Assert.Equal(phase, runner.Current.Phase);
		if (phase == WorkflowPhase.Deciding) {
			var resumedToken = await _decider.Started.Reader.ReadAsync();
			Assert.False(resumedToken.IsCancellationRequested);
		} else {
			Assert.Equal(phase == WorkflowPhase.Open ? WorkflowContext.Fresh : WorkflowContext.Continue, runner.Current.Kickoff!.Context);
		}
	}

	[Fact]
	public void AConversationEndingAfterJumpCannotPauseItsSuccessorEvenAtTheSameStage() {
		using var runner = Runner();
		var run = runner.Start("flow", Json("{}"));
		var previous = runner.Execution!;
		runner.Jump(run.Id, "a");
		var successor = runner.Execution!;
		Assert.NotSame(previous, successor);
		previous.ConversationEnded("Old controller finally closed");
		Assert.Equal(WorkflowPhase.Open, runner.Current!.Phase);
		Assert.Null(runner.Current.PausedReason);
		Assert.Same(successor, runner.Execution);
		runner.Cancel(run.Id);
		successor.ConversationEnded("Stopped after cancellation");
		Assert.Equal(WorkflowPhase.Cancelled, runner.Current.Phase);
	}

	[Fact]
	public async Task StageCallsRetainVisitAndRoundAcrossRevisionsAndJumps() {
		using var runner = Runner();
		var run = runner.Start("flow", Json("{}"));
		var visit = runner.Execution!;
		var delayed = visit.Capture();
		var complete = visit.Capture();
		complete.Complete(Json("{\"note\":\"n\"}"), "done");
		Assert.Same(visit, runner.Execution);
		Assert.Throws<WorkflowException>(() => delayed.Complete(Json("{\"note\":\"n\"}"), "duplicate"));
		runner.Answer(run.Id, "revise", "change it");
		Assert.Same(visit, runner.Execution);
		Assert.Throws<WorkflowException>(() => delayed.Complete(Json("{\"note\":\"n\"}"), "old round"));
		var revision = visit.Capture();
		revision.Fail("needs help");
		runner.Resume(run.Id);
		Assert.Same(visit, runner.Execution);
		runner.Jump(run.Id, "a");
		Assert.NotSame(visit, runner.Execution);
		Assert.False(await visit.Retired);
		Assert.Throws<WorkflowException>(() => revision.Fail("old visit"));
	}

	[Fact]
	public void AFixedSelfTransitionStartsANewVisitRatherThanARevision() {
		_dir.WriteFile("worktree/.weavie/workflows/loop.yaml", """
			id: loop
			description: loop
			stages:
			  - id: a
			    run: { prompt: again }
			    then: a
			""");
		using var runner = Runner();
		runner.Start("loop", Json("{}"));
		var first = runner.Execution!;
		var call = first.Capture();
		call.Complete(Json("{}"), "done");
		Assert.NotSame(first, runner.Execution);
		Assert.Equal(first.Stage.Id, runner.Execution!.Stage.Id);
		Assert.Throws<WorkflowException>(() => call.Complete(Json("{}"), "late"));
	}
}
