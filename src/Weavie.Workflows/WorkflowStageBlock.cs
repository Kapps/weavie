using System.Text;

namespace Weavie.Workflows;

/// <summary>The short block each agent turn carries while a run is live, so the stage survives compaction.</summary>
internal static class WorkflowStageBlock {
	/// <summary>The block for <paramref name="run"/>, or empty when no run is live.</summary>
	public static string Compose(WorkflowRun? run, WorkflowDefinition? definition, WorkflowVocabulary words) {
		if (run is null || run.IsFinished || definition is null) {
			return "";
		}

		var stage = definition.Stages.First(s => s.Id == run.Stage);
		var text = new StringBuilder()
			.Append($"[{words.Host} workflow] Run {run.Id} of workflow \"{run.WorkflowId}\" is at stage \"{run.Stage}\" ")
			.Append($"({definition.Stages.ToList().IndexOf(stage) + 1} of {definition.Stages.Count}). ");
		text.Append(run.Phase switch {
			WorkflowPhase.Open => OpenInstructions(stage, words),
			WorkflowPhase.Asking => AnswerRules(run, words),
			WorkflowPhase.Deciding => $"{words.Host} is deciding what comes next; wait for its next message.",
			WorkflowPhase.Paused => $"The run is paused: {run.PausedReason} Only the user resumes, jumps, or cancels it ({words.RunCommand} {words.Resume}, {words.JumpToStage}, or {words.Cancel} with run \"{run.Id}\"), and only when they say so.",
			_ => "",
		});
		return text.ToString();
	}

	private static string OpenInstructions(WorkflowStage stage, WorkflowVocabulary words) {
		string outputs = stage.Run.Outputs.Count == 0
			? "with no outputs ({})"
			: "with outputs " + string.Join(", ", stage.Run.Outputs.Select(o => $"\"{o.Name}\" ({o.Type.ToString().ToLowerInvariant()})"));
		return $"Do the stage's work, talking with the user when it needs them. When it is truly finished, call {words.CompleteStage} {outputs} and a short summary of what you did and found (decisions read it), then end your turn; {words.Host} starts the next stage. If you can't finish it, call {words.FailStage} with the reason.";
	}

	private static string AnswerRules(WorkflowRun run, WorkflowVocabulary words) {
		var asked = run.Asked!;
		return $"{words.Host} is asking the user: \"{asked.Question}\" (answers: {string.Join(", ", asked.Choices)})"
			+ (asked.Reason.Length == 0 ? ". " : $"; {asked.Reason}. ")
			+ $"Never answer for the user. Only when the user explicitly gives one of those answers, call {words.RunCommand} "
			+ $"{words.Answer} with run \"{run.Id}\", their choice, and their own words as feedback (empty when they gave none).";
	}
}
