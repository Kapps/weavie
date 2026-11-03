namespace Weavie.Workflows;

/// <summary>The host's names the engine writes into agent-facing text: its own name and the tools and commands a run uses.</summary>
public sealed record WorkflowVocabulary {
	/// <summary>The host's name, as the agent and user know it.</summary>
	public required string Host { get; init; }

	/// <summary>The tool the agent calls to complete the open stage.</summary>
	public required string CompleteStage { get; init; }

	/// <summary>The tool the agent calls when it can't finish the open stage.</summary>
	public required string FailStage { get; init; }

	/// <summary>The tool that runs a host command by id.</summary>
	public required string RunCommand { get; init; }

	/// <summary>The command that answers the question a run is asking the user.</summary>
	public required string Answer { get; init; }

	/// <summary>The command that resumes a paused run.</summary>
	public required string Resume { get; init; }

	/// <summary>The command that moves a run to a stage.</summary>
	public required string JumpToStage { get; init; }

	/// <summary>The command that cancels a run.</summary>
	public required string Cancel { get; init; }
}
