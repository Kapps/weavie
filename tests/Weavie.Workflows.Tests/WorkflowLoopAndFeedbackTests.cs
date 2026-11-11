using System.Text.Json;
using Weavie.Workflows;
using Xunit;

namespace Weavie.Workflows.Tests;

public sealed class WorkflowLoopAndFeedbackTests {
	private const string Loops = """
		id: loops
		description: loops
		stages:
		  - id: review
		    run: { prompt: review, outputs: { findings: text } }
		    then:
		      ask: "next?"
		      choices: { fix: fix, retest: retest, ok: wrap }
		      limit: 2
		  - id: fix
		    run: { prompt: fix }
		    then: review
		  - id: retest
		    run: { prompt: retest }
		    then: review
		  - id: wrap
		    run: { prompt: wrap, outputs: { left: list } }
		    then:
		      ask: "anything left?"
		      choices: { more: review, none: done }
		""";

	private const string Feedback = """
		id: feedback
		description: feedback
		stages:
		  - id: design
		    run: { prompt: design }
		    then:
		      ask: approve?
		      by: user
		      choices: { approve: tasks, skip: done, polish: polish }
		  - id: polish
		    if: { ask: "polish it?", by: user }
		    run: { prompt: polish, outputs: { x: text } }
		    then: done
		  - id: tasks
		    if: "is the design big?"
		    run: { prompt: "write tasks from {{polish.x}}" }
		""";

	private static WorkflowTransitions Machine(string text, string id) {
		var read = WorkflowReader.Read(text, id);
		return new(read.Definition ?? throw new InvalidOperationException(string.Join("\n", read.Diagnostics)), 3, true, 0.8, Path.GetTempPath(), TestWorkflows.Words);
	}

	private static WorkflowStep Done(WorkflowTransitions machine, WorkflowRun run, string outputs) =>
		machine.Complete(run, JsonDocument.Parse(outputs).RootElement, "summary").Step;

	private static T Single<T>(WorkflowStep step) where T : WorkflowEffect => Assert.Single(step.Effects.OfType<T>());

	[Fact]
	public void AlternatingModelAnswersStillReachTheLimit() {
		var machine = Machine(Loops, "loops");
		var run = machine.Start("r-1", "p", [], Path.GetTempPath(), null, WorkflowPrompt.None, []).Run;
		run = machine.Resolve(Done(machine, run, """{"findings":"a"}""").Run, new WorkflowDecisionResult.Answered("fix", 1), 0.8).Run;
		run = machine.Resolve(Done(machine, Done(machine, run, "{}").Run, """{"findings":"b"}""").Run, new WorkflowDecisionResult.Answered("retest", 1), 0.8).Run;
		Assert.Equal("retest", run.Stage);
		var capped = machine.Resolve(Done(machine, Done(machine, run, "{}").Run, """{"findings":"c"}""").Run, new WorkflowDecisionResult.Answered("fix", 1), 0.8);
		Assert.Equal(WorkflowPhase.Asking, capped.Run.Phase);
		Assert.Contains("2 times in a row", capped.Run.Asked!.Reason);
	}

	[Fact]
	public void AnotherStagesDecisionStartsTheCountOver() {
		var machine = Machine(Loops, "loops");
		var run = machine.Start("r-1", "p", [], Path.GetTempPath(), null, WorkflowPrompt.None, []).Run;
		run = machine.Resolve(Done(machine, run, """{"findings":"a"}""").Run, new WorkflowDecisionResult.Answered("fix", 1), 0.8).Run;
		run = machine.Resolve(Done(machine, Done(machine, run, "{}").Run, """{"findings":"b"}""").Run, new WorkflowDecisionResult.Answered("ok", 1), 0.8).Run;
		run = machine.Resolve(Done(machine, run, """{"left":["x"]}""").Run, new WorkflowDecisionResult.Answered("more", 1), 0.8).Run;
		Assert.Equal(("review", false), (run.Stage, run.Streaks.ContainsKey("review")));
		run = machine.Resolve(Done(machine, run, """{"findings":"c"}""").Run, new WorkflowDecisionResult.Answered("fix", 1), 0.8).Run;
		Assert.Equal(("fix", 1), (run.Stage, run.Streaks["review"]));
	}

	[Fact]
	public void FeedbackReachesATargetStageWhoseIfTheModelDecides() {
		var machine = Machine(Feedback, "feedback");
		var asking = Done(machine, machine.Start("r-1", "p", [], Path.GetTempPath(), null, WorkflowPrompt.None, []).Run, "{}");
		var deciding = machine.Answer(asking.Run, "approve", "keep it small");
		Assert.Contains("keep it small", Single<WorkflowEffect.Decide>(deciding).Plan.Summary);
		var opened = machine.Resolve(deciding.Run, new WorkflowDecisionResult.Answered("yes", 1), 0.8);
		Assert.Equal(WorkflowPhase.Paused, opened.Run.Phase);
		Assert.Contains("keep it small", opened.Run.Note);
	}

	[Fact]
	public void FeedbackOnAnIfAnsweredNoReachesTheFollowingStage() {
		var machine = Machine(Feedback, "feedback");
		var asking = Done(machine, machine.Start("r-1", "p", [], Path.GetTempPath(), null, WorkflowPrompt.None, []).Run, "{}");
		var polish = machine.Answer(asking.Run, "polish", "");
		Assert.Equal((WorkflowPhase.Asking, "polish"), (polish.Run.Phase, polish.Run.Stage));
		var deciding = machine.Answer(polish.Run, "no", "do the tasks instead");
		Assert.Equal("tasks", deciding.Run.Stage);
		Assert.Contains("do the tasks instead", Single<WorkflowEffect.Decide>(deciding).Plan.Summary);
	}

	[Fact]
	public void FeedbackWithNoStageLeftIsInTheFinishedNotice() {
		var machine = Machine(Feedback, "feedback");
		var asking = Done(machine, machine.Start("r-1", "p", [], Path.GetTempPath(), null, WorkflowPrompt.None, []).Run, "{}");
		var done = machine.Answer(asking.Run, "skip", "not worth it");
		Assert.Equal(WorkflowPhase.Done, done.Run.Phase);
		Assert.Contains("not worth it", Single<WorkflowEffect.Notify>(done).Message);
	}

	[Fact]
	public void AnAnsweredIfIsNotAskedAgainOnResume() {
		var machine = Machine(Feedback, "feedback");
		var asking = Done(machine, machine.Start("r-1", "p", [], Path.GetTempPath(), null, WorkflowPrompt.None, []).Run, "{}");
		var paused = machine.Resolve(machine.Answer(asking.Run, "approve", "").Run, new WorkflowDecisionResult.Answered("yes", 1), 0.8);
		Assert.Equal((WorkflowPhase.Paused, null), (paused.Run.Phase, paused.Run.Decision));
		var resumed = machine.Resume(paused.Run);
		Assert.Equal(WorkflowPhase.Paused, resumed.Run.Phase);
		Assert.DoesNotContain(resumed.Effects, e => e is WorkflowEffect.Decide);
	}
}
