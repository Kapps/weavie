using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Workflows;

namespace Weavie.Core.Workflows;

/// <summary>
/// Checks a workflow's agents against what the session's provider advertises and words every refusal; the model is
/// the axis in the <c>model</c> category, and axes in the <c>mode</c> category are never the workflow's to change.
/// </summary>
public static class WorkflowAgentControls {
	private const string ModelCategory = "model";
	private const string ModeCategory = "mode";

	/// <summary>
	/// Why a session of <paramref name="provider"/> can't run <paramref name="definition"/> with its roles filled by
	/// <paramref name="roles"/>, or <see langword="null"/>; a role <paramref name="roles"/> doesn't fill is the resolver's
	/// to word. With <paramref name="axes"/> (null while the controls aren't ready) it also checks the model, and the
	/// options whose model is the current one; the rest are checked when their stage opens.
	/// </summary>
	public static string? Refusal(
		WorkflowDefinition definition, IReadOnlyList<WorkflowAgent> roles, AgentProviderInfo provider, IReadOnlyList<AgentControlAxis>? axes) {
		ArgumentNullException.ThrowIfNull(definition);
		ArgumentNullException.ThrowIfNull(provider);
		bool structured = provider.Capabilities.HasFlag(AgentProviderCapabilities.StructuredPane);
		foreach (var stage in definition.Stages) {
			if (stage.Run.Context != WorkflowContext.Continue && !structured) {
				return $"Stage \"{stage.Id}\" requires its own conversation; {provider.Name} runs in a terminal.";
			}
			if (Asks(stage) is not { } asks) continue;
			if (!structured) {
				return $"Stage \"{stage.Id}\" runs on {asks}, but {provider.Name} runs in a terminal, where a stage can't "
					+ "choose its model; run the workflow in a native agent session.";
			}
			if (definition.AgentOf(stage, roles) is not { } agent) continue;
			string? problem = agent.Provider != provider.Id
				? $"Stage \"{stage.Id}\" runs on {asks} of provider {agent.Provider}, so workflow {definition.Id} runs "
					+ $"only in {agent.Provider} sessions; this session runs {provider.Id}."
				: axes is null ? null
				: ModelProblem(stage.Id, agent, axes)
					?? OptionsProblem(stage.Id, agent, axes, agent.Model is null || agent.Model == ModelAxis(axes)?.Value);
			if (problem is not null) return problem;
		}

		return null;
	}

	/// <summary>Why a role <paramref name="provider"/> can't fill names the first stage that asks for it.</summary>
	public static string NoModelChoice(WorkflowDefinition definition, string role, AgentProviderInfo provider) {
		ArgumentNullException.ThrowIfNull(definition);
		ArgumentNullException.ThrowIfNull(provider);
		var stage = definition.Stages.First(s => s.Run.Role == role);
		return $"Stage \"{stage.Id}\" runs on role \"{role}\", but {provider.Name} offers no model to choose.";
	}

	/// <summary>
	/// Why <paramref name="definition"/> can't start: each of its <paramref name="gaps"/> in a session of
	/// <paramref name="provider"/>, and what the agent reading it does next: ask the user, never choose for them.
	/// </summary>
	public static string Unfilled(
		WorkflowDefinition definition, IReadOnlyList<WorkflowRoleGap> gaps, AgentProviderInfo provider, IReadOnlyList<AgentControlOption>? offered) {
		ArgumentNullException.ThrowIfNull(definition);
		ArgumentNullException.ThrowIfNull(gaps);
		ArgumentNullException.ThrowIfNull(provider);
		var each = gaps.Select(gap => gap.MissingModel is { } missing
			? $"role \"{gap.Role}\" is saved as model \"{missing}\", which {provider.Name} no longer offers"
			: $"role \"{gap.Role}\" has no agent for {provider.Id} sessions");
		string offers = offered is null ? "" : $" {provider.Name} offers {Ids(offered.Select(o => o.Id))}.";
		return $"Workflow {definition.Id} can't start: {string.Join("; ", each)}. Ask the user which model and options each of these "
			+ $"roles should use; never choose for them.{offers} Then save each with {CoreCommands.WorkflowSetRole} (role, provider "
			+ $"\"{provider.Id}\", agent) and start the workflow again.";
	}

	/// <summary>The line by a role's picker when its saved model is gone.</summary>
	public static string Stale(string missingModel) => $"The saved model \"{missingModel}\" is no longer offered.";

	// What a stage asks to run on, for refusals: its pinned agent or its role.
	private static string? Asks(WorkflowStage stage) =>
		stage.Run.Agent is { } agent ? $"agent \"{agent}\"" : stage.Run.Role is { } role ? $"role \"{role}\"" : null;

	/// <summary>The model <paramref name="agent"/> sets for stage <paramref name="stage"/>, or <see langword="null"/> when it names none.</summary>
	public static AgentControlValue? Model(string stage, WorkflowAgent agent, IReadOnlyList<AgentControlAxis> axes) {
		ArgumentNullException.ThrowIfNull(agent);
		if (ModelProblem(stage, agent, axes) is { } problem) throw new AgentControlException(problem);
		return agent.Model is null ? null : new AgentControlValue(ModelAxis(axes)!.Id, agent.Model);
	}

	/// <summary>Throws <see cref="AgentControlException"/> when <paramref name="axes"/> don't offer an option <paramref name="agent"/> sets.</summary>
	public static void CheckOptions(string stage, WorkflowAgent agent, IReadOnlyList<AgentControlAxis> axes) {
		ArgumentNullException.ThrowIfNull(agent);
		if (OptionsProblem(stage, agent, axes, true) is { } problem) {
			throw new AgentControlException(problem);
		}
	}

	/// <summary>The model axis, or <see langword="null"/> when the agent advertises none.</summary>
	public static AgentControlAxis? ModelAxis(IReadOnlyList<AgentControlAxis> axes) => axes.FirstOrDefault(a => a.Category == ModelCategory);

	/// <summary>The current value of every axis a run may change: all but the <c>mode</c> category.</summary>
	public static Dictionary<string, string> Own(IReadOnlyList<AgentControlAxis> axes) =>
		axes.Where(Changeable).ToDictionary(a => a.Id, a => a.Value, StringComparer.Ordinal);

	/// <summary>Whether a run may change <paramref name="axis"/>.</summary>
	public static bool Changeable(AgentControlAxis axis) {
		ArgumentNullException.ThrowIfNull(axis);
		return axis.Category != ModeCategory;
	}

	/// <summary>The transcript line for <paramref name="subject"/>: the model, then each other axis <paramref name="held"/> names.</summary>
	public static string Line(string subject, IReadOnlyList<AgentControlAxis> axes, IReadOnlyCollection<string> held) {
		var model = ModelAxis(axes);
		IEnumerable<string> parts = [
			.. model is null ? [] : new[] { model.ValueLabel },
			.. axes.Where(a => a != model && held.Contains(a.Id)).Select(a => $"{a.Label} {a.ValueLabel}"),
		];
		return $"Workflow: {subject} on {string.Join(", ", parts)}";
	}

	private static string? ModelProblem(string stage, WorkflowAgent agent, IReadOnlyList<AgentControlAxis> axes) =>
		agent.Model is not { } model ? null
		: ModelAxis(axes) is not { } axis ? $"Stage \"{stage}\" asks for model \"{model}\", but the agent advertises no model option."
		: axis.Options.Any(o => o.Id == model) ? null
		: $"Stage \"{stage}\" asks for model \"{model}\"; the agent advertises {Ids(axis.Options.Select(o => o.Id))}.";

	private static string? OptionsProblem(string stage, WorkflowAgent agent, IReadOnlyList<AgentControlAxis> axes, bool current) =>
		agent.Options.Select(o => OptionProblem(stage, o.Key, o.Value, axes, current)).FirstOrDefault(p => p is not null);

	// Without the stage's model in effect only the categories are known, so an unknown option or value can't be judged yet.
	private static string? OptionProblem(string stage, string id, string value, IReadOnlyList<AgentControlAxis> axes, bool current) =>
		axes.FirstOrDefault(a => a.Id == id) switch {
			{ Category: ModelCategory } => $"Stage \"{stage}\" sets option \"{id}\", the model; name it as the agent's model instead.",
			{ Category: ModeCategory } => $"Stage \"{stage}\" sets option \"{id}\", a permission mode; a workflow can't change what the "
				+ "agent may do without asking.",
			_ when !current => null,
			null => $"Stage \"{stage}\" sets option \"{id}\", which the agent doesn't advertise; it advertises "
				+ $"{Ids(axes.Where(Changeable).Where(a => a.Category != ModelCategory).Select(a => a.Id))}.",
			var axis when axis.Options.All(o => o.Id != value) =>
				$"Stage \"{stage}\" sets option \"{id}\" to \"{value}\"; the agent advertises {Ids(axis.Options.Select(o => o.Id))} for it.",
			_ => null,
		};

	private static string Ids(IEnumerable<string> ids) => string.Join(", ", ids) is { Length: > 0 } list ? list : "none";
}
