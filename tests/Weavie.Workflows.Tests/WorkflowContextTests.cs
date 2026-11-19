using Xunit;

namespace Weavie.Workflows.Tests;

public sealed partial class WorkflowRunnerTests {
	[Theory]
	[InlineData(WorkflowContext.Fork)]
	[InlineData(WorkflowContext.Fresh)]
	public void IndependentCompletionReportsToMainWhileRevisionAndResumeKeepTheStageConversation(WorkflowContext context) {
		_dir.WriteFile("worktree/.weavie/workflows/flow.yaml", Flow.Replace("run: { prompt: \"do a\"", $"context: {context.ToString().ToLowerInvariant()}\n    run: {{ prompt: \"do a\"", StringComparison.Ordinal));
		using var runner = Runner();
		var run = runner.Start("flow", Json("{}"));
		var execution = runner.Execution!;
		Assert.NotNull(run.Kickoff!.Conversation);
		Assert.True(runner.TryClaimKickoff(run.Kickoff));
		Assert.Contains("runs in its own conversation", runner.StageBlock());
		Assert.DoesNotContain("Do the stage's work", runner.StageBlock());
		Assert.Contains("Do the stage's work", execution.Compose(false));
		var rejected = execution.Capture();
		Assert.Contains("missing output", rejected.Complete(Json("{}"), "invalid"));
		Assert.Empty(runner.Current!.Unreported);
		var completed = execution.Capture();
		string response = completed.Complete(Json("{\"note\":\"review findings\"}"), "Checked the implementation");
		Assert.DoesNotContain("Put that question", response);
		Assert.Contains("End your turn", response);
		Assert.Same(execution, runner.Execution);
		var question = runner.Current!.Kickoff!;
		Assert.Null(question.Conversation);
		Assert.Contains("note = review findings", question.Text);
		Assert.Contains("Summary: Checked the implementation", question.Text);
		Assert.Contains("now asks the user: approve review findings?", question.Text);
		Assert.Contains("approve review findings?", runner.StageBlock());
		Assert.Contains("asking the user in the main conversation", execution.Compose(false));
		Assert.Equal(runner.Current.Unreported, _store.Run!.Unreported);
		Assert.True(runner.TryClaimKickoff(question));
		Assert.Empty(_store.Run!.Unreported);
		runner.Answer(run.Id, "revise", "Include concurrency");
		Assert.Same(execution, runner.Execution);
		Assert.Throws<WorkflowException>(() => completed.Complete(Json("{\"note\":\"old\"}"), "old round"));
		Assert.NotNull(runner.Current!.Kickoff!.Conversation);
		Assert.Contains("Include concurrency", runner.Current.Kickoff.Text);
		Assert.DoesNotContain("Checked the implementation", runner.Current.Kickoff.Text);
		var revision = execution.Capture();
		revision.Fail("Need more evidence");
		runner.Resume(run.Id);
		Assert.Same(execution, runner.Execution);
		Assert.NotNull(runner.Current!.Kickoff!.Conversation);
		Assert.Equal("a", runner.Current.Kickoff.Opens);
	}

	[Fact]
	public void EachVisitNamesTheConversationItUsesAndSaysWhenThatConversationContinues() {
		_dir.WriteFile("worktree/.weavie/workflows/flow.yaml", """
			id: flow
			description: Reuse.
			stages:
			  - id: review
			    context: fresh
			    run: { prompt: Review. }
			    outputs: { report: text }
			  - id: fix
			    run: { prompt: Fix. }
			  - id: recheck
			    context: { reuse: review }
			    run: { prompt: Recheck. }
			    outputs: { report: text }
			""");
		using var runner = Runner();
		var run = runner.Start("flow", Json("{}"));
		var review = runner.Execution!;
		Assert.Equal("review", review.Conversation!.Opener.Id);
		Assert.DoesNotContain("opened again", review.Compose(false));
		Assert.Contains("Stage \"review\" has opened again; your earlier work on it is above.", review.Compose(true));
		runner.Jump(run.Id, "fix");
		Assert.Null(runner.Execution!.Conversation);
		Assert.Equal("", review.Compose(true));
		runner.Jump(run.Id, "recheck");
		var recheck = runner.Execution!;
		Assert.NotNull(runner.Current!.Kickoff!.Conversation);
		Assert.Equal("review", recheck.Conversation!.Opener.Id);
		Assert.Contains("Stage \"recheck\" continues the conversation of stage \"review\"; that work is above.", recheck.Compose(true));
		Assert.DoesNotContain("continues the conversation", runner.StageBlock());
	}

	[Fact]
	public void AContinuingSkillStageSaysSoInItsBlockAfterItsCommand() {
		_dir.WriteFile("worktree/.weavie/workflows/flow.yaml", """
			id: flow
			description: Skill reuse.
			stages:
			  - id: review
			    context: fresh
			    run: { skill: review, args: the change }
			""");
		using var runner = Runner();
		var run = runner.Start("flow", Json("{}"));
		Assert.Equal("/review the change", run.Kickoff!.Text);
		Assert.Contains("Stage \"review\" has opened again", runner.Execution!.Compose(true));
		Assert.DoesNotContain("/review", runner.Execution.Compose(true));
	}

	[Fact]
	public void ConsecutiveIndependentStagesAccumulateReportsAndPreserveTheNextMainSkill() {
		_dir.WriteFile("worktree/.weavie/workflows/flow.yaml", """
			id: flow
			description: Child reports.
			stages:
			  - id: review
			    context: fresh
			    run: { prompt: Review. }
			    outputs: { findings: list }
			  - id: verify
			    context: fork
			    run: { prompt: Verify. }
			    outputs: { result: text }
			  - id: publish
			    run: { skill: publish, args: release }
			""");
		using var runner = Runner();
		var run = runner.Start("flow", Json("{}"));
		Assert.True(runner.TryClaimKickoff(run.Kickoff!));
		var first = runner.Execution!.Capture();
		first.Complete(Json("{\"findings\":[\"race\",\"leak\"]}"), "Reviewed");
		var second = runner.Current!.Kickoff!;
		Assert.NotNull(second.Conversation);
		Assert.EndsWith("\n\nVerify.", second.Text);
		Assert.Contains("Summary: Reviewed", second.Text);
		Assert.Contains("Summary: Reviewed", runner.Current.Unreported);
		Assert.True(runner.TryClaimKickoff(second));
		Assert.Contains("Summary: Reviewed", runner.Current.Unreported);
		var next = runner.Execution!.Capture();
		next.Complete(Json("{\"result\":\"all checks pass\"}"), "Verified");
		var main = runner.Current!.Kickoff!;
		Assert.Null(main.Conversation);
		Assert.Equal("publish", main.Command);
		Assert.Equal("publish", main.Opens);
		Assert.StartsWith("/publish release\n\n", main.Text);
		Assert.Contains("findings = - race\n- leak", main.Text);
		Assert.Contains("result = all checks pass", main.Text);
		Assert.Contains("Summary: Reviewed", main.Text);
		Assert.Contains("Summary: Verified", main.Text);
		Assert.True(runner.TryClaimKickoff(main));
		Assert.Empty(runner.Current.Unreported);
	}

	[Fact]
	public async Task FinalReportSurvivesRestartUntilClaimed() {
		_dir.WriteFile("worktree/.weavie/workflows/flow.yaml", """
			id: flow
			description: Final report.
			stages:
			  - id: review
			    context: fresh
			    run: { prompt: Review. }
			    outputs: { note: text }
			""");
		WorkflowKickoff report;
		using (var runner = Runner()) {
			var run = runner.Start("flow", Json("{}"));
			Assert.True(runner.TryClaimKickoff(run.Kickoff!));
			var execution = runner.Execution!;
			var call = execution.Capture();
			call.Complete(Json("{\"note\":\"no findings\"}"), "Reviewed everything");
			Assert.Equal(WorkflowPhase.Done, runner.Current!.Phase);
			Assert.True(await execution.Retired);
			report = runner.Current.Kickoff!;
			Assert.Null(report.Conversation);
			Assert.Empty(report.Opens);
			Assert.Contains("note = no findings", report.Text);
			Assert.Contains("Workflow flow finished.", report.Text);
			Assert.Equal(report, _store.Run!.Kickoff);
		}
		_port.Kickoffs.Clear();
		using (var restored = Runner()) {
			Assert.Null(restored.Execution);
			Assert.Equal([report.Text], _port.Kickoffs);
			Assert.True(restored.TryClaimKickoff(report));
			Assert.Empty(_store.Run!.Unreported);
		}
		_port.Kickoffs.Clear();
		using var claimed = Runner();
		Assert.Empty(_port.Kickoffs);
	}

	[Fact]
	public void AReportSurvivesAnUnsentQuestionBeingReplacedByAJump() {
		_dir.WriteFile("worktree/.weavie/workflows/flow.yaml", Flow.Replace("run: { prompt: \"do a\"", "context: fresh\n    run: { prompt: \"do a\"", StringComparison.Ordinal));
		using var runner = Runner();
		var run = runner.Start("flow", Json("{}"));
		var call = runner.Execution!.Capture();
		call.Complete(Json("{\"note\":\"not delivered yet\"}"), "Result waits for main");
		var question = runner.Current!.Kickoff!;
		runner.Jump(run.Id, "b");
		Assert.False(runner.TryClaimKickoff(question));
		Assert.Null(runner.Current!.Kickoff!.Conversation);
		Assert.Contains("Result waits for main", runner.Current.Kickoff.Text);
		Assert.Contains("do b", runner.Current.Kickoff.Text);
	}

	[Fact]
	public async Task CancellingRetiresTheStageImmediatelyAndStillReportsItsAcceptedResult() {
		_dir.WriteFile("worktree/.weavie/workflows/flow.yaml", Flow.Replace("run: { prompt: \"do a\"", "context: fresh\n    run: { prompt: \"do a\"", StringComparison.Ordinal));
		using var runner = Runner();
		var run = runner.Start("flow", Json("{}"));
		var execution = runner.Execution!;
		var call = execution.Capture();
		call.Complete(Json("{\"note\":\"accepted result\"}"), "Work finished");
		runner.Cancel(run.Id);
		Assert.False(await execution.Retired);
		Assert.Equal(WorkflowPhase.Cancelled, runner.Current!.Phase);
		Assert.Null(runner.Current.Kickoff!.Conversation);
		Assert.Contains("accepted result", runner.Current.Kickoff.Text);
		Assert.Contains("cancelled", runner.Current.Kickoff.Text);
		Assert.DoesNotContain("now asks the user", runner.Current.Kickoff.Text);
	}
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void RestartPausesADeliveredIndependentStageWithoutGuessingAContinuation(bool asking) {
		_dir.WriteFile("worktree/.weavie/workflows/flow.yaml", Flow.Replace("run: { prompt: \"do a\"", "context: fresh\n    run: { prompt: \"do a\"", StringComparison.Ordinal));
		using (var runner = Runner()) {
			var run = runner.Start("flow", Json("{}"));
			Assert.True(runner.TryClaimKickoff(run.Kickoff!));
			if (asking) runner.Execution!.Capture().Complete(Json("{\"note\":\"result\"}"), "Reviewed");
		}
		_port.Kickoffs.Clear();
		using var restored = Runner();
		Assert.Equal(WorkflowPhase.Paused, restored.Current!.Phase);
		Assert.Contains("conversation ended", restored.Current.PausedReason, StringComparison.Ordinal);
		Assert.Empty(_port.Kickoffs);
		restored.Resume(restored.Current.Id);
		Assert.Equal(asking ? WorkflowPhase.Asking : WorkflowPhase.Open, restored.Current.Phase);
		Assert.NotNull(restored.Current.Kickoff);
	}

}
