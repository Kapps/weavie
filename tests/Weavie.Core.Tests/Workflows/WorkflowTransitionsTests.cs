using System.Text.Json;
using Weavie.Core.Workflows;
using Xunit;

namespace Weavie.Core.Tests.Workflows;

public sealed class WorkflowTransitionsTests {
	private const string Flow = """
		id: flow
		description: test flow
		inputs:
		  task: text
		  note: text?
		stages:
		  - id: plan
		    when: "is {{inputs.task}} big?"
		    else: build
		    do:
		      prompt: "plan {{inputs.task}}"
		      outputs:
		        dir: text
		    gate: approve
		  - id: build
		    do: { skill: implement, args: "{{inputs.task}} {{inputs.note}}" }
		    next:
		      prompt: "blocking?"
		      choices: { yes: fix, no: done }
		      max: 1
		  - id: fix
		    do: { prompt: "fix it" }
		    next: build
		""";

	private static readonly WorkflowDefinition Definition = WorkflowReader.Read(Flow, "flow").Definition
		?? throw new InvalidOperationException(string.Join("\n", WorkflowReader.Read(Flow, "flow").Diagnostics));

	private static readonly WorkflowTransitions Machine = new(Definition, 2, Path.GetTempPath());

	private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

	private static WorkflowStep Started() =>
		Machine.Start("r-1", "p", WorkflowTransitions.ReadInputs(Definition, Json("""{"task":"auth"}"""), Path.GetTempPath()));

	private static WorkflowDecisionRequest Decision(WorkflowStep step) =>
		Assert.IsType<WorkflowEffect.Decide>(Assert.Single(step.Effects)).Request;

	private static string Kickoff(WorkflowStep step) => Assert.IsType<WorkflowEffect.Kickoff>(Assert.Single(step.Effects)).Text;

	private static WorkflowStep OpenPlan() => Machine.Resolve(Started().Run, WorkflowDecisionResult.Chose("yes"));

	private static WorkflowStep OpenBuild() => Machine.Resolve(Started().Run, WorkflowDecisionResult.Chose("no"));

	[Fact]
	public void StartAsksTheFirstStagesWhenWithRenderedInputs() {
		var step = Started();
		Assert.Equal((WorkflowPhase.Deciding, WorkflowDecisionKind.When), (step.Run.Phase, step.Run.Deciding));
		Assert.Equal("is auth big?", Decision(step).Question);
		Assert.Equal(["yes", "no"], Decision(step).Choices);
	}

	[Fact]
	public void WhenYesOpensTheStageAndNoJumpsToElse() {
		var yes = OpenPlan();
		Assert.Equal(("plan", WorkflowPhase.Open, "plan auth"), (yes.Run.Stage, yes.Run.Phase, Kickoff(yes)));
		var no = OpenBuild();
		Assert.Equal(("build", "/implement auth"), (no.Run.Stage, Kickoff(no)));
		Assert.Equal("/implement auth", no.Run.Kickoff);
	}

	[Fact]
	public void BadOutputsAreListedCountedAndPauseAtTheCap() {
		var open = OpenPlan().Run;
		var first = Machine.Complete(open, Json("""{"dir":3,"extra":"x"}"""));
		Assert.Equal(["output \"dir\": expected text", "stage \"plan\" declares no output \"extra\""], first.Errors);
		Assert.Equal((WorkflowPhase.Open, 1), (first.Step.Run.Phase, first.Step.Run.Attempts));
		var second = Machine.Complete(first.Step.Run, Json("{}"));
		Assert.Equal(["missing output \"dir\" (text)"], second.Errors);
		Assert.Equal(WorkflowPhase.Paused, second.Step.Run.Phase);
		Assert.IsType<WorkflowEffect.Notify>(Assert.Single(second.Step.Effects));
	}

	[Fact]
	public void GatedStageWaitsForApprovalAndApproveAdvances() {
		var done = Machine.Complete(OpenPlan().Run, Json("""{"dir":"docs/x"}"""));
		Assert.Empty(done.Errors);
		Assert.Equal(WorkflowPhase.AwaitingApproval, done.Step.Run.Phase);
		Assert.Equal("docs/x", done.Step.Run.Outputs["plan"]["dir"].Text);
		var approved = Machine.Approve(done.Step.Run);
		Assert.Equal(("build", "/implement auth"), (approved.Run.Stage, Kickoff(approved)));
	}

	[Fact]
	public void RejectReopensTheStageWithTheFeedback() {
		var waiting = Machine.Complete(OpenPlan().Run, Json("""{"dir":"docs/x"}""")).Step.Run;
		var rejected = Machine.Reject(waiting, "split auth into two");
		Assert.Equal(WorkflowPhase.Open, rejected.Run.Phase);
		Assert.Contains("split auth into two", Kickoff(rejected));
	}

	[Fact]
	public void ChoiceNextFollowsTheAnswerAndPausesPastMax() {
		var building = OpenBuild().Run;
		var deciding = Machine.Complete(building, Json("{}")).Step;
		Assert.Equal("blocking?", Decision(deciding).Question);
		var fix = Machine.Resolve(deciding.Run, WorkflowDecisionResult.Chose("yes"));
		Assert.Equal(("fix", 1), (fix.Run.Stage, fix.Run.EdgeCounts["build→fix"]));
		var again = Machine.Complete(Machine.Complete(fix.Run, Json("{}")).Step.Run, Json("{}")).Step;
		var capped = Machine.Resolve(again.Run, WorkflowDecisionResult.Chose("yes"));
		Assert.Equal(WorkflowPhase.Paused, capped.Run.Phase);
		Assert.Contains("limit of 1", capped.Run.PausedReason);
		var resumed = Machine.Resume(capped.Run);
		Assert.Equal((WorkflowPhase.Deciding, WorkflowDecisionKind.Next), (resumed.Run.Phase, resumed.Run.Deciding));
	}

	[Theory]
	[InlineData("unsure", "unsure")]
	[InlineData("maybe", "isn't one of its choices")]
	public void UnsureOrUnknownAnswersPause(string answer, string reason) {
		var deciding = Machine.Complete(OpenBuild().Run, Json("{}")).Step.Run;
		var step = Machine.Resolve(deciding, WorkflowDecisionResult.Chose(answer));
		Assert.Equal(WorkflowPhase.Paused, step.Run.Phase);
		Assert.Contains(reason, step.Run.PausedReason);
	}

	[Fact]
	public void FailedDecisionPausesAndResumeAsksTheWhenAgain() {
		var paused = Machine.Resolve(Started().Run, WorkflowDecisionResult.Failed("TimedOut: slow"));
		Assert.Contains("TimedOut: slow", paused.Run.PausedReason);
		var resumed = Machine.Resume(paused.Run);
		Assert.Equal("is auth big?", Decision(resumed).Question);
	}

	[Fact]
	public void FinishingTheLastStageEndsTheRun() {
		var deciding = Machine.Complete(OpenBuild().Run, Json("{}")).Step.Run;
		var done = Machine.Resolve(deciding, WorkflowDecisionResult.Chose("no"));
		Assert.Equal(WorkflowPhase.Done, done.Run.Phase);
		Assert.True(done.Run.IsFinished);
	}

	[Fact]
	public void FailStagePausesAndResumeRestartsTheStage() {
		var paused = Machine.Fail(OpenBuild().Run, "no credentials");
		Assert.Contains("no credentials", paused.Run.PausedReason);
		Assert.Null(paused.Run.Kickoff);
		Assert.Equal("/implement auth", Kickoff(Machine.Resume(paused.Run)));
	}

	[Fact]
	public void JumpOpensAStageRegardlessOfItsWhenAndCancelEnds() {
		var jumped = Machine.Jump(Started().Run, "plan");
		Assert.Equal(("plan", WorkflowPhase.Open), (jumped.Run.Stage, jumped.Run.Phase));
		Assert.Equal(WorkflowPhase.Cancelled, Machine.Cancel(jumped.Run).Run.Phase);
		Assert.Throws<WorkflowException>(() => Machine.Jump(jumped.Run, "nope"));
	}

	[Fact]
	public void TransitionsOutOfPhaseAreRefused() {
		var open = OpenBuild().Run;
		Assert.Throws<WorkflowException>(() => Machine.Approve(open));
		Assert.Throws<WorkflowException>(() => Machine.Resume(open));
		Assert.Throws<WorkflowException>(() => Machine.Resolve(open, WorkflowDecisionResult.Chose("yes")));
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
			    do: { prompt: "a", outputs: { x: text } }
			  - id: b
			    do: { prompt: "use {{a.x}}" }
			""", "refs").Definition!;
		var machine = new WorkflowTransitions(definition, 3, Path.GetTempPath());
		var run = machine.Start("r-2", "p", []).Run;
		var paused = machine.Jump(run, "b");
		Assert.Equal(WorkflowPhase.Paused, paused.Run.Phase);
		Assert.Contains("{{a.x}}", paused.Run.PausedReason);
		Assert.Equal(WorkflowPhase.Open, jumped.Run.Phase);
	}

	[Fact]
	public void InputsAreTypeCheckedAndUnknownOrMissingOnesListed() {
		var ex = Assert.Throws<WorkflowException>(() =>
			WorkflowTransitions.ReadInputs(Definition, Json("""{"bogus":1}"""), Path.GetTempPath()));
		Assert.Contains("missing input \"task\"", ex.Message);
		Assert.Contains("unknown input \"bogus\"", ex.Message);
	}
}
