using System.Text.Json;
using Weavie.Workflows;
using Xunit;

namespace Weavie.Workflows.Tests;

public sealed class WorkflowConfidenceTests {
	private static (WorkflowTransitions Machine, WorkflowStep Step, WorkflowDecisionPlan Plan) Decision(bool condition, string extra, double global) {
		string question = $"{{ ask: Is it complete?, {extra} }}";
		string source = $$"""
			id: check
			description: Check evidence.
			stages:
			  - id: work
			    {{(condition ? "if: " + question : "")}}
			    run: { prompt: Work. }
			    then: { ask: Is it complete?, {{extra}} choices: { yes: done, no: work, unsure: done }, limit: 1 }
			""";
		var read = WorkflowReader.ReadProposal(source);
		Assert.NotNull(read.Definition);
		var machine = new WorkflowTransitions(read.Definition, 3, true, global, Path.GetTempPath(), TestWorkflows.Words);
		var step = machine.Start("r", "source", [], Path.GetTempPath());
		if (!condition) step = machine.Complete(step.Run, JsonDocument.Parse("{}").RootElement, "Completed checks.").Step;
		return (machine, step, Assert.IsType<WorkflowEffect.Decide>(Assert.Single(step.Effects)).Plan);
	}

	[Theory]
	[InlineData(true, 0.8, true)]
	[InlineData(false, 0.8, true)]
	[InlineData(true, 0.79, false)]
	[InlineData(false, 0.79, false)]
	public void EqualityRoutesAndLowerConfidencePreservesQuestionAndChoices(bool condition, double confidence, bool routes) {
		var (machine, step, plan) = Decision(condition, "", 0.8);
		var resolved = machine.Resolve(step.Run, new WorkflowDecisionResult.Answered("yes", confidence), plan.MinimumConfidence);
		Assert.Equal(!routes, resolved.Run.Phase == WorkflowPhase.Asking);
		if (!routes) {
			Assert.Equal(step.Run.Asked!.Question, resolved.Run.Asked!.Question);
			Assert.Equal(step.Run.Asked.Choices, resolved.Run.Asked.Choices);
			Assert.Contains("0.79", resolved.Run.Asked.Reason);
			Assert.Contains("0.8", resolved.Run.Asked.Reason);
			Assert.Empty(resolved.Run.Streaks);
			var answered = machine.Answer(resolved.Run, "yes", "Human checked.");
			Assert.NotEqual(WorkflowPhase.Asking, answered.Run.Phase);
		}
	}

	[Theory]
	[InlineData(true, "confidence: 0,", 0)]
	[InlineData(false, "confidence: 0,", 0)]
	[InlineData(true, "confidence: 1,", 1)]
	[InlineData(false, "confidence: 1,", 1)]
	[InlineData(false, "", 0.8)]
	public void PlansCaptureOverrideOrGlobalThreshold(bool condition, string extra, double expected) {
		var (machine, step, plan) = Decision(condition, extra, 0.8);
		Assert.Equal(expected, plan.MinimumConfidence);
		Assert.NotEqual(WorkflowPhase.Asking,
			machine.Resolve(step.Run, new WorkflowDecisionResult.Answered("yes", expected), plan.MinimumConfidence).Run.Phase);
	}

	[Theory]
	[InlineData(double.NaN)]
	[InlineData(double.PositiveInfinity)]
	[InlineData(double.NegativeInfinity)]
	[InlineData(-0.01)]
	[InlineData(1.01)]
	public void InvalidConfidenceIsNeverClamped(double confidence) {
		var (machine, step, plan) = Decision(false, "confidence: 0,", 0.8);
		var resolved = machine.Resolve(step.Run, new WorkflowDecisionResult.Answered("yes", confidence), plan.MinimumConfidence);
		Assert.Equal(WorkflowPhase.Asking, resolved.Run.Phase);
		Assert.Contains("invalid", resolved.Run.Asked!.Reason);
	}

	[Fact]
	public void RefusalFailureAndUnknownChoicesEscalateWithoutCountingALoop() {
		var (machine, step, plan) = Decision(false, "", 0.8);
		WorkflowDecisionResult[] results = [new WorkflowDecisionResult.Refused("refused"), new WorkflowDecisionResult.Failed("failed"), new WorkflowDecisionResult.Answered("unknown", 1)];
		foreach (var result in results) {
			var resolved = machine.Resolve(step.Run, result, plan.MinimumConfidence);
			Assert.Equal(WorkflowPhase.Asking, resolved.Run.Phase);
			Assert.NotEmpty(resolved.Run.Asked!.Reason);
			Assert.Empty(resolved.Run.Streaks);
		}
		Assert.Equal(WorkflowPhase.Done, machine.Resolve(step.Run, new WorkflowDecisionResult.Answered("unsure", 1), plan.MinimumConfidence).Run.Phase);
	}
}
