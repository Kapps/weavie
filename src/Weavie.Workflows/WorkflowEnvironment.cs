namespace Weavie.Workflows;

/// <summary>What a session gives its runner besides the ports: where it works, the host's names, and its live settings.</summary>
public sealed record WorkflowEnvironment {
	/// <summary>The session worktree; relative file values resolve against it.</summary>
	public required string Worktree { get; init; }

	/// <summary>The host's names, written into agent-facing text.</summary>
	public required WorkflowVocabulary Words { get; init; }

	/// <summary>The documents directory of a new run, from its id.</summary>
	public required Func<string, string> DocumentsDirectory { get; init; }

	/// <summary>How many rejected completions of one stage pause the run.</summary>
	public required Func<int> MaxStageAttempts { get; init; }

	/// <summary>Whether a question put to the user opens the document it is about.</summary>
	public required Func<bool> OpenReviewItem { get; init; }

	/// <summary>The model confidence a decision needs to route without the user, unless its question sets its own.</summary>
	public required Func<double> DecisionConfidence { get; init; }
}
