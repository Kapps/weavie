using System.Text;

namespace Weavie.Core.Workflows;

/// <summary>The short block each agent turn carries while a run is live, so the stage survives compaction.</summary>
public static class WorkflowStageBlock {
	/// <summary>The block for <paramref name="run"/>, or empty when no run is live.</summary>
	public static string Compose(WorkflowRun? run, WorkflowDefinition? definition) {
		if (run is null || run.IsFinished || definition is null) {
			return "";
		}

		var stage = definition.Stages.First(s => s.Id == run.Stage);
		var text = new StringBuilder()
			.Append($"[Weavie workflow] Run {run.Id} of workflow \"{run.WorkflowId}\" is at stage \"{run.Stage}\" ")
			.Append($"({definition.Stages.ToList().IndexOf(stage) + 1} of {definition.Stages.Count}). ");
		text.Append(run.Phase switch {
			WorkflowPhase.Open => OpenInstructions(stage),
			WorkflowPhase.Asking => AnswerRules(run),
			WorkflowPhase.Deciding => "Weavie is deciding what comes next; wait for its next message.",
			WorkflowPhase.Paused => $"The run is paused: {run.PausedReason} Only the user resumes, jumps, or cancels it (runCommand weavie.workflow.resume, weavie.workflow.jumpToStage, or weavie.workflow.cancel with run \"{run.Id}\"), and only when they say so.",
			_ => "",
		});
		return text.ToString();
	}

	private static string OpenInstructions(WorkflowStage stage) {
		string outputs = stage.Run.Outputs.Count == 0
			? "with no outputs ({})"
			: "with outputs " + string.Join(", ", stage.Run.Outputs.Select(o => $"\"{o.Name}\" ({o.Type.ToString().ToLowerInvariant()})"));
		return $"Do the stage's work, talking with the user when it needs them. When it is truly finished, call completeStage {outputs} and a short summary of what you did and found (decisions read it), then end your turn; Weavie starts the next stage. If you can't finish it, call failStage with the reason.";
	}

	private static string AnswerRules(WorkflowRun run) {
		var asked = run.Asked!;
		return $"Weavie is asking the user: \"{asked.Question}\" (answers: {string.Join(", ", asked.Choices)})"
			+ (asked.Reason.Length == 0 ? ". " : $"; {asked.Reason}. ")
			+ "Never answer for the user. Only when the user explicitly gives one of those answers, call runCommand "
			+ $"weavie.workflow.answer with run \"{run.Id}\", their choice, and their own words as feedback (empty when they gave none).";
	}
}
