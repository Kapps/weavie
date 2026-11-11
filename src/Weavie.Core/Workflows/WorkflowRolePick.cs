using System.Globalization;
using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Inference;
using Weavie.Workflows;

namespace Weavie.Core.Workflows;

/// <summary>A start's choice for one role: the model, and the provider's option values chosen with it.</summary>
/// <param name="Model">The provider's model id.</param>
/// <param name="Options">The provider's config option values by option id.</param>
public sealed record WorkflowRolePick(string Model, IReadOnlyDictionary<string, string> Options) {
	/// <summary>Reads a start's <c>roles</c> argument: role to <c>{ model, options }</c>; <see langword="null"/> is none.</summary>
	public static IReadOnlyDictionary<string, WorkflowRolePick> Read(JsonElement? roles) {
		var picks = new Dictionary<string, WorkflowRolePick>(StringComparer.Ordinal);
		if (roles is not { ValueKind: not JsonValueKind.Null } given) return picks;
		if (given.ValueKind != JsonValueKind.Object) throw new WorkflowException("'roles' must be an object of role names to { model, options }.");
		foreach (var role in given.EnumerateObject()) {
			picks[role.Name] = new(WeavieWorkflows.Required(role.Value, "model", $"roles.{role.Name}.model"), ReadOptions(role.Value));
		}
		return picks;
	}

	/// <summary>The <c>options</c> of <paramref name="agent"/>. Agents write values as JSON text, booleans, or numbers; the provider's value ids are text.</summary>
	public static Dictionary<string, string> ReadOptions(JsonElement agent) {
		var options = new Dictionary<string, string>(StringComparer.Ordinal);
		if (!agent.TryGetProperty("options", out var given)) return options;
		if (given.ValueKind != JsonValueKind.Object) throw new WorkflowException("'options' must be an object of option ids to value ids.");
		foreach (var option in given.EnumerateObject()) {
			options[option.Name] = option.Value.ValueKind switch {
				JsonValueKind.String when option.Value.GetString() is { Length: > 0 } text => text,
				JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number => option.Value.GetRawText().ToLower(CultureInfo.InvariantCulture),
				_ => throw new WorkflowException($"Option '{option.Name}' needs a value id."),
			};
		}
		return options;
	}
}

/// <summary>A role's pickers, and why the agent couldn't be asked for all of them.</summary>
/// <param name="Axes">The model picker, then the effort and Fast Mode the chosen model offers.</param>
/// <param name="Error">Why the options aren't known, or <see langword="null"/>.</param>
public sealed record WorkflowRoleControls(IReadOnlyList<AgentControlAxis> Axes, string? Error);

/// <summary>
/// The pickers that fill a role in one provider's sessions: the model, then the effort and Fast Mode that model
/// offers. Each option id is the provider's own; an empty id leaves that option as the session has it.
/// </summary>
public static class WorkflowRoleAxes {
	/// <summary>The category of the model picker, the one a role can't leave unset.</summary>
	public const string ModelCategory = "model";

	/// <summary>
	/// Asks <paramref name="options"/> (null when the provider can't be asked) what <paramref name="model"/> offers and
	/// builds the pickers; with no model chosen there is nothing to ask. A failed ask keeps the model picker.
	/// </summary>
	public static async Task<WorkflowRoleControls> AskAsync(
		AgentProviderInfo provider, IInferenceProvider? options, IReadOnlyList<AgentControlOption> models, string model, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(provider);
		if (model.Length == 0) return new(Build(models, model, []), null);
		if (options is null) return new(Build(models, model, []), $"{provider.Name} doesn't report its options.");
		try {
			var selected = (await options.ProbeInferenceControlsAsync(model, ct).ConfigureAwait(false)).Selected;
			// A model the agent refuses leaves its default selected, and those options aren't this model's.
			return selected.FirstOrDefault(control => control.Category == ModelCategory)?.Value == model
				? new(Build(models, model, selected), null)
				: new(Build(models, model, []), $"{provider.Name} didn't take model \"{model}\", so its options aren't known.");
		} catch (Exception ex) when (ex is not OperationCanceledException) {
			return new(Build(models, model, []), ex.Message);
		}
	}

	/// <summary>
	/// Builds the pickers from the provider's <paramref name="models"/>, the chosen <paramref name="model"/> (empty
	/// for none), and the controls the provider advertises with that model <paramref name="selected"/>.
	/// </summary>
	public static IReadOnlyList<AgentControlAxis> Build(IReadOnlyList<AgentControlOption> models, string model, IReadOnlyList<AgentControlAxis> selected) {
		ArgumentNullException.ThrowIfNull(models);
		ArgumentNullException.ThrowIfNull(selected);
		AgentControlAxis?[] axes = [
			new AgentControlAxis {
				Id = ModelCategory,
				Label = "Model",
				Category = ModelCategory,
				Kind = "select",
				Value = model,
				ValueLabel = models.FirstOrDefault(option => option.Id == model)?.Label ?? "Choose a model",
				Options = models,
			},
			Optional(InferenceControlAxes.Effort(selected), "Effort"),
			Optional(InferenceControlAxes.FastMode(selected), "Fast mode"),
		];
		return [.. axes.OfType<AgentControlAxis>()];
	}

	// Leads with "Default (High)": what the model runs with when the role sets nothing.
	private static AgentControlAxis? Optional(AgentControlAxis? axis, string label) => axis is null ? null : axis with {
		Label = label,
		Value = "",
		Options = [new AgentControlOption { Id = "", Label = $"Default ({axis.ValueLabel})" }, .. axis.Options],
	};
}
