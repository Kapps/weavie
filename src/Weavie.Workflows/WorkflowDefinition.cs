using System.Text.Json.Serialization;

namespace Weavie.Workflows;

/// <summary>A validated workflow: a tree of stages the host runs and the agent fills.</summary>
public sealed record WorkflowDefinition {
	/// <summary>The workflow id, equal to its file name.</summary>
	public required string Id { get; init; }

	/// <summary>One line shown when choosing a workflow.</summary>
	public required string Description { get; init; }

	/// <summary>Values a run starts with.</summary>
	public required IReadOnlyList<WorkflowInputSpec> Inputs { get; init; }

	/// <summary>The agents steps name, in file order.</summary>
	public required IReadOnlyList<WorkflowAgent> Agents { get; init; }

	/// <summary>The root list of stages, in file order.</summary>
	public required IReadOnlyList<WorkflowStage> Stages { get; init; }

	/// <summary>The exact file text this definition was read from.</summary>
	public required string SourceText { get; init; }

	/// <summary>Every stage at any depth, each before the stages it holds.</summary>
	public IEnumerable<WorkflowStage> All => Walk(Stages);

	/// <summary>Whether any stage's work or question reads <c>{{run.prompt}}</c>.</summary>
	public bool UsesPrompt => All
		.SelectMany(stage => new[] { stage.Run?.Prompt, stage.Run?.Args, stage.If?.Ask, (stage.Then as WorkflowThen.Decide)?.Question.Ask })
		.Any(template => template?.References.Any(reference => reference.IsPrompt) == true);

	/// <summary>The roles steps ask for, in first-use order.</summary>
	public IReadOnlyList<string> Roles => [.. All.Select(stage => stage.Role).OfType<string>().Distinct(StringComparer.Ordinal)];

	/// <summary>The stage with <paramref name="id"/> at any depth; throws when there is none.</summary>
	public WorkflowStage Stage(string id) => TryStage(id) ?? throw new WorkflowException($"Workflow {Id} has no stage \"{id}\".");

	/// <summary>The stage with <paramref name="id"/> at any depth, or <see langword="null"/>.</summary>
	public WorkflowStage? TryStage(string id) => Index.TryGetValue(id, out var entry) ? entry.Stage : null;

	/// <summary>The stage whose list holds <paramref name="stage"/>, or <see langword="null"/> at the root.</summary>
	public WorkflowStage? Parent(WorkflowStage stage) {
		ArgumentNullException.ThrowIfNull(stage);
		return Index[stage.Id].Parent;
	}

	/// <summary>The stages beside <paramref name="stage"/>, itself included, in file order.</summary>
	public IReadOnlyList<WorkflowStage> List(WorkflowStage stage) => Parent(stage)?.Stages ?? Stages;

	/// <summary>
	/// The agent that runs <paramref name="stage"/>: its conversation's pinned agent, or its role's agent in
	/// <paramref name="roles"/>; <see langword="null"/> for the session's own model or a role <paramref name="roles"/> doesn't fill.
	/// </summary>
	public WorkflowAgent? AgentOf(WorkflowStage stage, IReadOnlyList<WorkflowAgent> roles) {
		ArgumentNullException.ThrowIfNull(stage);
		ArgumentNullException.ThrowIfNull(roles);
		stage = Opener(stage) ?? stage;
		return stage.Agent is { } name ? Agents.First(a => a.Name == name)
			: stage.Role is { } role ? roles.FirstOrDefault(a => a.Name == role)
			: null;
	}

	/// <summary>
	/// The stage whose conversation runs <paramref name="stage"/>'s turns: itself for fork or fresh, the sibling it
	/// reuses, else its parent's; <see langword="null"/> for main.
	/// </summary>
	public WorkflowStage? Opener(WorkflowStage stage) {
		ArgumentNullException.ThrowIfNull(stage);
		return stage.Reuse is { } reused ? List(stage).First(s => s.Id == reused)
			: stage.Context != WorkflowContext.Continue ? stage
			: Parent(stage) is { } parent ? Opener(parent)
			: null;
	}

	private IReadOnlyDictionary<string, (WorkflowStage Stage, WorkflowStage? Parent)> Index => field ??= BuildIndex();

	private Dictionary<string, (WorkflowStage, WorkflowStage?)> BuildIndex() {
		var index = new Dictionary<string, (WorkflowStage, WorkflowStage?)>(StringComparer.Ordinal);
		void Add(IReadOnlyList<WorkflowStage> list, WorkflowStage? parent) {
			foreach (var stage in list) {
				index.TryAdd(stage.Id, (stage, parent));
				Add(stage.Stages, stage);
			}
		}
		Add(Stages, null);
		return index;
	}

	private static IEnumerable<WorkflowStage> Walk(IReadOnlyList<WorkflowStage> list) =>
		list.SelectMany(stage => new[] { stage }.Concat(Walk(stage.Stages)));
}

/// <summary>A named agent: provider-native ids the host applies, opaque to the engine.</summary>
public sealed record WorkflowAgent {
	/// <summary>The name steps refer to.</summary>
	public required string Name { get; init; }

	/// <summary>The provider id.</summary>
	public required string Provider { get; init; }

	/// <summary>The provider's model id, or <see langword="null"/> for the model as it stands.</summary>
	public required string? Model { get; init; }

	/// <summary>The provider's config option values by option id, in file order.</summary>
	public required IReadOnlyDictionary<string, string> Options { get; init; }
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

/// <summary>A typed value a stage reports, or one a stage with stages copies from them.</summary>
public sealed record WorkflowOutputSpec {
	/// <summary>The output's name.</summary>
	public required string Name { get; init; }

	/// <summary>The output's type; a copied output has its source's.</summary>
	public required WorkflowValueType Type { get; init; }

	/// <summary>The output of a stage inside this one that it copies, or <see langword="null"/> for one its agent reports.</summary>
	public required WorkflowReference? Source { get; init; }
}

/// <summary>One stage of a workflow: work it runs, or stages it holds.</summary>
public sealed record WorkflowStage {
	/// <summary>Unique within the workflow file.</summary>
	public required string Id { get; init; }

	/// <summary>A yes/no question whose no skips the stage, or <see langword="null"/> to always run.</summary>
	public required WorkflowQuestion? If { get; init; }

	/// <summary>The list the stage repeats over, once per entry, or <see langword="null"/>.</summary>
	public required WorkflowTemplate? Foreach { get; init; }

	/// <summary>How the conversation that runs this stage opens; a reusing stage has the context of the stage it reuses.</summary>
	public required WorkflowContext Context { get; init; }

	/// <summary>The sibling whose conversation this stage continues, or <see langword="null"/>.</summary>
	public required string? Reuse { get; init; }

	/// <summary>The name of the agent its conversation runs on, or <see langword="null"/>.</summary>
	public required string? Agent { get; init; }

	/// <summary>The role the user fills for its conversation, or <see langword="null"/>.</summary>
	public required string? Role { get; init; }

	/// <summary>The work, or <see langword="null"/> for a stage that holds stages.</summary>
	public required WorkflowWork? Run { get; init; }

	/// <summary>The stages it holds, in file order; empty for a stage with work.</summary>
	public required IReadOnlyList<WorkflowStage> Stages { get; init; }

	/// <summary>The typed values the stage has when it completes.</summary>
	public required IReadOnlyList<WorkflowOutputSpec> Outputs { get; init; }

	/// <summary>What follows this stage.</summary>
	public required WorkflowThen Then { get; init; }

	/// <summary>Whether the stage repeats per entry of a list.</summary>
	public bool Repeats => Foreach is not null;
}

/// <summary>A stage's work: a prompt, or a skill run as its slash command.</summary>
public sealed record WorkflowWork {
	/// <summary>The instruction text, or <see langword="null"/> for a skill.</summary>
	public required WorkflowTemplate? Prompt { get; init; }

	/// <summary>The skill name without its slash, or <see langword="null"/> for a prompt.</summary>
	public required string? Skill { get; init; }

	/// <summary>Text passed after the skill name, or <see langword="null"/>.</summary>
	public required WorkflowTemplate? Args { get; init; }
}

/// <summary>How a stage obtains its conversation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkflowContext>))]
public enum WorkflowContext {
	/// <summary>Run in the conversation the stage sits in.</summary>
	Continue,
	/// <summary>Own a fork of the conversation the stage sits in.</summary>
	Fork,
	/// <summary>Own a fresh conversation.</summary>
	Fresh,
}

/// <summary>Who answers a decision.</summary>
public enum WorkflowAnswerer {
	/// <summary>A tool-less decision model; a low-confidence or failed answer goes to the user.</summary>
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

	/// <summary>The model confidence override, or null to use the live setting.</summary>
	public required double? Confidence { get; init; }
}

/// <summary>What follows a stage.</summary>
public abstract record WorkflowThen {
	/// <summary>The following sibling in file order (done after the last).</summary>
	public sealed record Sequential : WorkflowThen;

	/// <summary>A sibling's id, or <see cref="WorkflowSchema.Done"/>.</summary>
	public sealed record Goto(string Target) : WorkflowThen;

	/// <summary>A decision whose answer names the next stage; <paramref name="Limit"/> caps an answer chosen in a row.</summary>
	public sealed record Decide(WorkflowQuestion Question, IReadOnlyList<WorkflowChoice> Choices, int? Limit) : WorkflowThen;
}

/// <summary>One answer of a decision and where it leads.</summary>
public sealed record WorkflowChoice(string Name, string Target, string? Description);
