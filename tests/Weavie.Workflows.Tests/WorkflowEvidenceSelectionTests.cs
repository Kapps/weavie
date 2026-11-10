using System.Text.Json;
using Xunit;

namespace Weavie.Workflows.Tests;

public sealed class WorkflowEvidenceSelectionTests : IDisposable {
	private const string Flow = """
		id: evidence
		description: evidence selection
		inputs:
		  docs: files?
		  directory: text?
		stages:
		  - id: draft
		    run: { prompt: Draft., outputs: { documents: files } }
		  - id: review
		    if: "Ready {{inputs.docs}} {{draft.documents}}?"
		    run: { prompt: Review., outputs: { documents: files, note: text } }
		    then:
		      ask: "Accept {{review.documents}} {{inputs.docs}} {{draft.documents}} {{review.documents}} {{review.note}}?"
		      choices: { accept: done, revise: review }
		""";
	private readonly TempDirectory _dir = new("weavie-evidence-selection");

	public void Dispose() => _dir.Dispose();

	private WorkflowTransitions Machine(string source) {
		var read = WorkflowReader.Read(source, "evidence");
		Assert.True(read.Definition is not null, string.Join("; ", read.Diagnostics));
		return new(read.Definition!, 3, true, 0.8, _dir.Path, TestWorkflows.Words);
	}

	private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;
	private static WorkflowDecisionPlan Plan(WorkflowStep step) => Assert.IsType<WorkflowEffect.Decide>(Assert.Single(step.Effects)).Plan;

	[Fact]
	public void IfAndThenSelectTypedReferencesInOrderAndKeepQuestionValuesAndSummary() {
		_dir.WriteFile("draft.md", "draft");
		_dir.WriteFile("review.md", "review");
		var machine = Machine(Flow);
		var run = machine.Start("r", "flow.yaml", new() { ["docs"] = new() { Items = ["./input.md", "draft.md"] } }, Path.GetTempPath()).Run;
		var condition = machine.Complete(run, Json("""{"documents":["./draft.md"]}"""), "Drafted.").Step;
		Assert.Equal(["input.md", "draft.md"], Plan(condition).FilePaths);
		Assert.Equal("Ready - ./input.md\n- draft.md - ./draft.md?", Plan(condition).Question);
		Assert.Empty(Plan(condition).Outputs);
		run = machine.Resolve(condition.Run, new WorkflowDecisionResult.Answered("yes", 1), 0.8).Run;
		var decision = machine.Complete(run, Json("""{"documents":["review.md","./review.md"],"note":"note.md"}"""), "Reviewed.").Step;
		var plan = Plan(decision);
		Assert.Equal(["review.md", "input.md", "draft.md"], plan.FilePaths);
		Assert.Contains("- review.md\n- ./review.md", plan.Question);
		Assert.EndsWith(" note.md?", plan.Question, StringComparison.Ordinal);
		Assert.Equal("- review.md\n- ./review.md", plan.Outputs["documents"]);
		Assert.Equal("note.md", plan.Outputs["note"]);
		Assert.Equal("Reviewed.", plan.Summary);
	}

	[Theory]
	[InlineData("Review docs/review.md and README.md?")]
	[InlineData("Review {{inputs.directory}}/review.md and https://example.com/doc?")]
	public void LiteralAndComposedPathsAndUnreferencedOutputsSelectNothing(string question) {
		_dir.WriteFile("draft.md", "draft");
		var machine = Machine(Flow.Replace("Ready {{inputs.docs}} {{draft.documents}}?", question, StringComparison.Ordinal));
		var run = machine.Start("r", "flow.yaml", new() { ["directory"] = new() { Text = "docs" } }, Path.GetTempPath()).Run;
		var plan = Plan(machine.Complete(run, Json("""{"documents":["draft.md"]}"""), "Drafted.").Step);
		Assert.Empty(plan.FilePaths);
	}

	[Fact]
	public void OptionalAndEmptyFilesContributeNoPathsButMissingStageValuesStillPause() {
		var machine = Machine(Flow);
		var run = machine.Start("r", "flow.yaml", [], Path.GetTempPath()).Run;
		Assert.Empty(Plan(machine.Complete(run, Json("""{"documents":[]}"""), "Empty.").Step).FilePaths);
		var paused = machine.Resume(run with { Stage = "review", Phase = WorkflowPhase.Paused, Decision = WorkflowDecisionKind.If });
		Assert.Equal(WorkflowPhase.Paused, paused.Run.Phase);
		Assert.Contains("hasn't reported", paused.Run.PausedReason);
		Assert.DoesNotContain(paused.Effects, effect => effect is WorkflowEffect.Decide);
	}

	[Fact]
	public void InvalidPersistedPathsEscalateWithQuestionAndChoicesInsteadOfThrowing() {
		var machine = Machine(Flow);
		var run = machine.Start("r", "flow.yaml", new() { ["docs"] = new() { Items = ["../outside.md"] } }, Path.GetTempPath()).Run;
		var step = machine.Complete(run, Json("""{"documents":[]}"""), "Empty.").Step;
		Assert.Equal(WorkflowPhase.Asking, step.Run.Phase);
		Assert.Contains("../outside.md", step.Run.Asked!.Reason);
		Assert.Contains("Ready - ../outside.md", step.Run.Asked.Question);
		Assert.Equal(["yes", "no"], step.Run.Asked.Choices);
		Assert.DoesNotContain(step.Effects, effect => effect is WorkflowEffect.Decide);
	}

	[Fact]
	public void UserQuestionsDoNotPrepareEvidence() {
		var machine = Machine(Flow.Replace("if: \"Ready {{inputs.docs}} {{draft.documents}}?\"", "if: { ask: \"Ready {{inputs.docs}} {{draft.documents}}?\", by: user }", StringComparison.Ordinal));
		var run = machine.Start("r", "flow.yaml", new() { ["docs"] = new() { Items = ["../outside.md"] } }, Path.GetTempPath()).Run;
		var step = machine.Complete(run, Json("""{"documents":[]}"""), "Empty.").Step;
		Assert.Equal(WorkflowPhase.Asking, step.Run.Phase);
		Assert.Empty(step.Run.Asked!.Reason);
		Assert.DoesNotContain(step.Effects, effect => effect is WorkflowEffect.Decide);
	}
}
