using System.Text;

namespace Weavie.Workflows;

/// <summary>The short block each agent turn carries while a run is live, so the stage survives compaction.</summary>
internal static class WorkflowStageBlock {
	/// <summary>The block for <paramref name="run"/>, or empty when no run is live.</summary>
	public static string Compose(WorkflowRun? run, WorkflowDefinition? definition, WorkflowVocabulary words, bool ownsStage) {
		if (run is null || run.IsFinished || definition is null) {
			return "";
		}

		var stage = definition.Stages.First(s => s.Id == run.Stage);
		bool independent = stage.Run.Context != WorkflowContext.Continue;
		var text = new StringBuilder()
			.Append($"[{words.Host} workflow] Run {run.Id} of workflow \"{run.WorkflowId}\" is at stage \"{run.Stage}\" ")
			.Append($"({definition.Stages.ToList().IndexOf(stage) + 1} of {definition.Stages.Count}). ");
		if (independent && !ownsStage) text.Append($"The stage runs in its own conversation; only that conversation can call {words.CompleteStage} or {words.FailStage} for it. ");
		text.Append(run.Phase switch {
			WorkflowPhase.Open when independent && !ownsStage => $"Keep helping the user; {words.Host} will send its result here.",
			WorkflowPhase.Open => OpenInstructions(stage, words),
			WorkflowPhase.Asking when independent && ownsStage => $"{words.Host} is asking the user in the main conversation; wait for its answer.",
			WorkflowPhase.Asking => AnswerRules(run, words),
			WorkflowPhase.Deciding => $"{words.Host} is deciding what comes next; wait for its next message.",
			WorkflowPhase.Paused => $"The run is paused: {run.PausedReason} Only the user resumes, jumps, or cancels it ({words.RunCommand} {words.Resume}, {words.JumpToStage}, or {words.Cancel} with run \"{run.Id}\"), and only when they say so.",
			_ => "",
		});
		text.Append($" Shared documents: {run.DocumentsDirectory}. Sessions started from this run share this folder; use it for documents that must cross worktrees.");
		return text.ToString();
	}

	/// <summary>Tells the agent to put <paramref name="asked"/> to the user, opening what it is about when <paramref name="openItem"/>.</summary>
	public static string PutToUser(WorkflowAsked asked, WorkflowVocabulary words, bool openItem) =>
		$"{words.Host} now asks the user: {asked.Question} (answers: {string.Join(", ", asked.Choices)}). "
		+ (openItem
			? $"If the question is about a document or file, call {words.OpenFile} with preview, rendered, and focus true on the main one only, and link any others in your reply. "
			: "")
		+ "Put that question to the user with what they need to answer it, and end your turn.";

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
