using System.Text.Json;
using Weavie.Workflows;
using Xunit;

namespace Weavie.Workflows.Tests;

public sealed class WorkflowTransitionsTests {
	private const string Flow = """
		id: flow
		description: test flow
		inputs:
		  task: text
		  note: text?
		stages:
		  - id: plan
		    if: "is {{inputs.task}} big?"
		    run:
		      prompt: "plan {{inputs.task}}"
		      outputs:
		        dir: text
		    then:
		      ask: "approve {{plan.dir}}?"
		      by: user
		      choices: { approve: build, revise: plan }
		  - id: build
		    run: { skill: implement, args: "{{inputs.task}} {{inputs.note}}" }
		    then:
		      ask: "blocking in {{inputs.task}}?"
		      choices: { yes: fix, no: done }
		      limit: 1
		  - id: fix
		    run: { prompt: "fix it" }
		    then: build
		""";

	private static readonly WorkflowDefinition Definition = WorkflowReader.Read(Flow, "flow").Definition
		?? throw new InvalidOperationException(string.Join("\n", WorkflowReader.Read(Flow, "flow").Diagnostics));

	private static readonly WorkflowTransitions Machine = new(Definition, 2, true, 0.8, Path.GetTempPath(), TestWorkflows.Words);

	private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

	private static WorkflowStep Started() =>
		Machine.Start("r-1", "p", WorkflowTransitions.ReadInputs(Definition, Json("""{"task":"auth"}"""), Path.GetTempPath(), null), Path.GetTempPath(), null);

	private static WorkflowDecisionPlan Decision(WorkflowStep step) =>
		Assert.Single(step.Effects.OfType<WorkflowEffect.Decide>()).Plan;

	private static string Kickoff(WorkflowStep step) => Assert.Single(step.Effects.OfType<WorkflowEffect.Kickoff>()).Turn.Text;

	private static WorkflowStep OpenPlan() => Machine.Resolve(Started().Run, new WorkflowDecisionResult.Answered("yes", 1), 0.8);

	private static WorkflowStep OpenBuild() => Machine.Resolve(Started().Run, new WorkflowDecisionResult.Answered("no", 1), 0.8);

	private static WorkflowCompletion Complete(WorkflowRun run, string outputs) => Machine.Complete(run, Json(outputs), "did the work");

	private static WorkflowAsked Asked(WorkflowStep step) => step.Run.Asked!;

	[Fact]
	public void StartAsksTheFirstStagesIfWithRenderedInputs() {
		var step = Started();
		Assert.Equal((WorkflowPhase.Deciding, WorkflowDecisionKind.If), (step.Run.Phase, step.Run.Decision));
		Assert.Equal("is auth big?", Decision(step).Question);
		Assert.Equal(["yes", "no"], Decision(step).Choices.Select(c => c.Name));
	}

	[Fact]
	public void IfYesOpensTheStageAndNoFallsThroughToTheFollowingStage() {
		var yes = OpenPlan();
		Assert.Equal(("plan", WorkflowPhase.Open, "plan auth"), (yes.Run.Stage, yes.Run.Phase, Kickoff(yes)));
		var no = OpenBuild();
		Assert.Equal(("build", "/implement auth"), (no.Run.Stage, Kickoff(no)));
		Assert.Equal(new WorkflowKickoff { Context = WorkflowContext.Continue, Text = "/implement auth", Command = "implement", Opens = "build" }, no.Run.Kickoff);
		Assert.Equal("plan", yes.Run.Kickoff!.Opens);
	}

	[Fact]
	public void BadOutputsAreListedCountedAndPauseAtTheCap() {
		var open = OpenPlan().Run;
		var first = Complete(open, """{"dir":3,"extra":"x"}""");
		Assert.Equal(["output \"dir\": expected text", "stage \"plan\" declares no output \"extra\""], first.Errors);
		Assert.Equal((WorkflowPhase.Open, 1), (first.Step.Run.Phase, first.Step.Run.Attempts));
		var second = Complete(first.Step.Run, "{}");
		Assert.Equal(["missing output \"dir\" (text)"], second.Errors);
		Assert.Equal(WorkflowPhase.Paused, second.Step.Run.Phase);
		Assert.IsType<WorkflowEffect.Notify>(Assert.Single(second.Step.Effects));
	}

	[Fact]
	public void UserDecisionWaitsForTheAnswerWhichPicksTheNextStage() {
		var done = Complete(OpenPlan().Run, """{"dir":"docs/x"}""");
		Assert.Empty(done.Errors);
		Assert.Equal(WorkflowPhase.Asking, done.Step.Run.Phase);
		Assert.Equal(("approve docs/x?", "approve,revise", ""), (Asked(done.Step).Question, string.Join(",", Asked(done.Step).Choices), Asked(done.Step).Reason));
		Assert.Empty(done.Step.Effects);
		Assert.Equal("docs/x", done.Step.Run.Outputs["plan"]["dir"].Text);
		var approved = Machine.Answer(done.Step.Run, "approve", "");
		Assert.Equal(("build", "/implement auth"), (approved.Run.Stage, Kickoff(approved)));
		Assert.Null(approved.Run.Asked);
	}

	[Fact]
	public void AnAnswerLeadingBackToTheStageReopensItWithTheFeedback() {
		var waiting = Complete(OpenPlan().Run, """{"dir":"docs/x"}""").Step.Run;
		var revised = Machine.Answer(waiting, "revise", "split auth into two");
		Assert.Equal(("plan", WorkflowPhase.Open), (revised.Run.Stage, revised.Run.Phase));
		Assert.Contains("split auth into two", Kickoff(revised));
		Assert.Contains("approve docs/x?", Kickoff(revised));
		Assert.Equal("", revised.Run.Kickoff!.Opens);
		Assert.Equal("", Machine.Resolve(Started().Run, new WorkflowDecisionResult.Failed("down"), 0.8).Run.Kickoff!.Opens);
		Assert.Throws<WorkflowException>(() => Machine.Answer(waiting, "maybe", ""));
	}

	[Fact]
	public void ModelDecisionsReadTheOutputsAndTheSummary() {
		var deciding = Machine.Complete(OpenBuild().Run, Json("{}"), "two blocking bugs left").Step;
		Assert.Equal(("blocking in auth?", "two blocking bugs left"), (Decision(deciding).Question, Decision(deciding).Summary));
	}

	[Fact]
	public void ALimitCountsTheModelsAnswersInARowThenAsksTheUserWhoseAnswerResetsIt() {
		var deciding = Complete(OpenBuild().Run, "{}").Step;
		var fix = Machine.Resolve(deciding.Run, new WorkflowDecisionResult.Answered("yes", 1), 0.8);
		Assert.Equal(("fix", 1), (fix.Run.Stage, fix.Run.Streaks["build"]));
		var again = Complete(Complete(fix.Run, "{}").Step.Run, "{}").Step;
		var capped = Machine.Resolve(again.Run, new WorkflowDecisionResult.Answered("yes", 1), 0.8);
		Assert.Equal(WorkflowPhase.Asking, capped.Run.Phase);
		Assert.Contains("1 times in a row", Asked(capped).Reason);
		var answered = Machine.Answer(capped.Run, "yes", "");
		Assert.Equal("fix", answered.Run.Stage);
		Assert.Empty(answered.Run.Streaks);
		var next = Complete(Complete(answered.Run, "{}").Step.Run, "{}").Step;
		Assert.Equal("fix", Machine.Resolve(next.Run, new WorkflowDecisionResult.Answered("yes", 1), 0.8).Run.Stage);
	}

	[Theory]
	[InlineData("unsure", "invalid choice")]
	[InlineData("maybe", "invalid choice")]
	public void UndeclaredChoicesGoToTheUser(string answer, string reason) {
		var deciding = Complete(OpenBuild().Run, "{}").Step.Run;
		var step = Machine.Resolve(deciding, new WorkflowDecisionResult.Answered(answer, 1), 0.8);
		Assert.Equal(WorkflowPhase.Asking, step.Run.Phase);
		Assert.Equal("blocking in auth?", Asked(step).Question);
		Assert.Contains(reason, Asked(step).Reason);
		Assert.Contains("blocking in auth?", step.Run.Kickoff!.Text);
		Assert.Equal(WorkflowPhase.Done, Machine.Answer(step.Run, "no", "").Run.Phase);
	}

	[Fact]
	public void AUserQuestionAfterAnAnswerOwesTheAgentATurnACancelRetires() {
		var read = WorkflowReader.Read("""
			id: flow
			description: d
			stages:
			  - id: a
			    run: { prompt: "do a" }
			    then: { ask: "go?", by: user, choices: { yes: b, no: done } }
			  - id: b
			    if: { ask: "really?", by: user }
			    run: { prompt: "do b" }
			""", "flow");
		var machine = new WorkflowTransitions(read.Definition!, 3, true, 0.8, Path.GetTempPath(), TestWorkflows.Words);
		var asking = machine.Complete(machine.Start("r-1", "p", [], Path.GetTempPath(), null).Run, Json("{}"), "s").Step.Run;
		var next = machine.Answer(asking, "yes", "");
		Assert.Contains("really?", next.Run.Kickoff!.Text);
		Assert.Null(machine.Cancel(next.Run).Run.Kickoff);
	}

	[Fact]
	public void FailedDecisionAsksTheUserTheSameQuestion() {
		var asking = Machine.Resolve(Started().Run, new WorkflowDecisionResult.Failed("TimedOut: slow"), 0.8);
		Assert.Equal(("is auth big?", WorkflowPhase.Asking), (Asked(asking).Question, asking.Run.Phase));
		Assert.Contains("TimedOut: slow", Asked(asking).Reason);
		Assert.Equal("plan", Machine.Answer(asking.Run, "yes", "").Run.Stage);
	}

	[Fact]
	public void AModelAnswerBackToTheSameStageContinuesItWithAShortTurn() {
		var definition = WorkflowReader.Read("""
			id: loop
			description: d
			stages:
			  - id: a
			    run: { prompt: "do a", outputs: { left: list } }
			    then:
			      ask: "anything left in {{a.left}}?"
			      choices: { yes: a, no: done }
			""", "loop").Definition!;
		var machine = new WorkflowTransitions(definition, 3, true, 0.8, Path.GetTempPath(), TestWorkflows.Words);
		var deciding = machine.Complete(machine.Start("r-3", "p", [], Path.GetTempPath(), null).Run, Json("""{"left":["x"]}"""), "one left").Step;
		var again = machine.Resolve(deciding.Run, new WorkflowDecisionResult.Answered("yes", 1), 0.8);
		Assert.Equal(("a", WorkflowPhase.Open), (again.Run.Stage, again.Run.Phase));
		Assert.StartsWith("Host decided \"yes\"", Kickoff(again));
	}

	[Fact]
	public void FinishingTheLastStageEndsTheRun() {
		var deciding = Complete(OpenBuild().Run, "{}").Step.Run;
		var done = Machine.Resolve(deciding, new WorkflowDecisionResult.Answered("no", 1), 0.8);
		Assert.Equal(WorkflowPhase.Done, done.Run.Phase);
		Assert.True(done.Run.IsFinished);
	}

	[Fact]
	public void FailStagePausesAndResumeRestartsTheStage() {
		var paused = Machine.Fail(OpenBuild().Run, "no credentials");
		Assert.Contains("no credentials", paused.Run.PausedReason);
		Assert.Contains("no credentials", Assert.IsType<WorkflowEffect.Notify>(Assert.Single(paused.Effects)).Message);
		Assert.Null(paused.Run.Kickoff);
		Assert.Equal("/implement auth", Kickoff(Machine.Resume(paused.Run)));
	}

	[Fact]
	public void JumpOpensAStageRegardlessOfItsIfAndCancelEnds() {
		var jumped = Machine.Jump(Started().Run, "plan");
		Assert.Equal(("plan", WorkflowPhase.Open), (jumped.Run.Stage, jumped.Run.Phase));
		Assert.Equal(WorkflowPhase.Cancelled, Machine.Cancel(jumped.Run).Run.Phase);
		Assert.Throws<WorkflowException>(() => Machine.Jump(jumped.Run, "nope"));
	}

	[Fact]
	public void TransitionsOutOfPhaseAreRefused() {
		var open = OpenBuild().Run;
		Assert.Throws<WorkflowException>(() => Machine.Answer(open, "yes", ""));
		Assert.Throws<WorkflowException>(() => Machine.Resume(open));
		Assert.Throws<WorkflowException>(() => Machine.Resolve(open, new WorkflowDecisionResult.Answered("yes", 1), 0.8));
		Assert.Throws<WorkflowException>(() => Machine.Cancel(Machine.Cancel(open).Run));
	}

	[Fact]
	public void MissingOutputReferencePausesLoudly() {
		var jumped = Machine.Jump(Started().Run, "build");
		var definition = WorkflowReader.Read("""
			id: refs
			description: d
			stages:
			  - id: a
			    run: { prompt: "a", outputs: { x: text } }
			  - id: b
			    run: { prompt: "use {{a.x}}" }
			""", "refs").Definition!;
		var machine = new WorkflowTransitions(definition, 3, true, 0.8, Path.GetTempPath(), TestWorkflows.Words);
		var run = machine.Start("r-2", "p", [], Path.GetTempPath(), null).Run;
		var paused = machine.Jump(run, "b");
		Assert.Equal(WorkflowPhase.Paused, paused.Run.Phase);
		Assert.Contains("{{a.x}}", paused.Run.PausedReason);
		Assert.Equal(WorkflowPhase.Open, jumped.Run.Phase);
	}

	[Fact]
	public void InputsAreTypeCheckedAndUnknownOrMissingOnesListed() {
		var ex = Assert.Throws<WorkflowException>(() =>
			WorkflowTransitions.ReadInputs(Definition, Json("""{"bogus":1}"""), Path.GetTempPath(), null));
		Assert.Contains("missing input \"task\"", ex.Message);
		Assert.Contains("unknown input \"bogus\"", ex.Message);
	}
}
