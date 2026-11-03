namespace Weavie.Core.Workflows;

/// <summary>A validated workflow: ordered stages Weavie runs and the agent fills.</summary>
public sealed record WorkflowDefinition {
	/// <summary>The workflow id, equal to its file name.</summary>
	public required string Id { get; init; }

	/// <summary>One line shown when choosing a workflow.</summary>
	public required string Description { get; init; }

	/// <summary>Values a run starts with.</summary>
	public required IReadOnlyList<WorkflowInputSpec> Inputs { get; init; }

	/// <summary>The stages in file order.</summary>
	public required IReadOnlyList<WorkflowStage> Stages { get; init; }

	/// <summary>The exact file text this definition was read from.</summary>
	public required string SourceText { get; init; }
}

/// <summary>The type of a workflow input or output value.</summary>
public enum WorkflowValueType {
	/// <summary>Free text.</summary>
	Text,

	/// <summary>An absolute http(s) URL.</summary>
	Url,

	/// <summary>A list of text items.</summary>
	List,

	/// <summary>Worktree-relative paths of existing, non-empty files.</summary>
	Files,

	/// <summary>A pull request URL.</summary>
	Pr,
}

/// <summary>A declared workflow input.</summary>
public sealed record WorkflowInputSpec {
	/// <summary>The input's name.</summary>
	public required string Name { get; init; }

	/// <summary>The input's type.</summary>
	public required WorkflowValueType Type { get; init; }

	/// <summary>Whether a run may start without it.</summary>
	public required bool Optional { get; init; }
}

/// <summary>A typed value a stage reports.</summary>
public sealed record WorkflowOutputSpec {
	/// <summary>The output's name.</summary>
	public required string Name { get; init; }

	/// <summary>The output's type.</summary>
	public required WorkflowValueType Type { get; init; }
}

/// <summary>One stage of a workflow.</summary>
public sealed record WorkflowStage {
	/// <summary>Unique within the workflow.</summary>
	public required string Id { get; init; }

	/// <summary>A yes/no question whose no skips the stage, or <see langword="null"/> to always run.</summary>
	public required WorkflowQuestion? If { get; init; }

	/// <summary>The work.</summary>
	public required WorkflowWork Run { get; init; }

	/// <summary>What follows this stage.</summary>
	public required WorkflowThen Then { get; init; }
}

/// <summary>A stage's work: a prompt, or a skill run as its slash command.</summary>
public sealed record WorkflowWork {
	/// <summary>The instruction text, or <see langword="null"/> for a skill.</summary>
	public required WorkflowTemplate? Prompt { get; init; }

	/// <summary>The skill name without its slash, or <see langword="null"/> for a prompt.</summary>
	public required string? Skill { get; init; }

	/// <summary>Text passed after the skill name, or <see langword="null"/>.</summary>
	public required WorkflowTemplate? Args { get; init; }

	/// <summary>The typed values the stage must report.</summary>
	public required IReadOnlyList<WorkflowOutputSpec> Outputs { get; init; }
}

/// <summary>Who answers a decision.</summary>
public enum WorkflowAnswerer {
	/// <summary>A tool-less decision model; an unsure or failed answer goes to the user.</summary>
	Model,

	/// <summary>The user, through the agent.</summary>
	User,
}

/// <summary>A question whose answer is one of its choices; for <c>if</c> the choices are yes and no.</summary>
public sealed record WorkflowQuestion {
	/// <summary>The question; may reference values.</summary>
	public required WorkflowTemplate Ask { get; init; }

	/// <summary>Who answers it.</summary>
	public required WorkflowAnswerer By { get; init; }
}

/// <summary>What follows a stage.</summary>
public abstract record WorkflowThen {
	/// <summary>The following stage in file order (done after the last).</summary>
	public sealed record Sequential : WorkflowThen;

	/// <summary>A fixed stage id, or <see cref="WorkflowSchema.Done"/>.</summary>
	public sealed record Goto(string Target) : WorkflowThen;

	/// <summary>A decision whose answer names the next stage; <paramref name="Limit"/> caps an answer chosen in a row.</summary>
	public sealed record Decide(WorkflowQuestion Question, IReadOnlyList<WorkflowChoice> Choices, int? Limit) : WorkflowThen;
}

/// <summary>One answer of a decision and where it leads.</summary>
public sealed record WorkflowChoice(string Name, string Target);
