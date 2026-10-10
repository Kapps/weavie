using Weavie.Core.Agents;
using Weavie.Core.Configuration;

namespace Weavie.Core.Inference;

/// <summary>What one provider advertises for ad-hoc queries, in ACP configuration-option shape.</summary>
public sealed record InferenceControls {
	/// <summary>The controls a query with a blank profile runs with; their current values are the defaults.</summary>
	public required IReadOnlyList<AgentControlAxis> Defaults { get; init; }

	/// <summary>The controls once the configured model is applied, so model-dependent options are current.</summary>
	public required IReadOnlyList<AgentControlAxis> Selected { get; init; }
}

/// <summary>The suggestion profile pickers: each axis id is a setting key and each option id is a value for it.</summary>
public sealed record InferenceChoices {
	/// <summary>The agent, model, effort, and Fast Mode pickers, omitting ones the agent lacks.</summary>
	public required IReadOnlyList<AgentControlAxis> Axes { get; init; }

	/// <summary>A configured value the agent doesn't offer, which makes every query fail until it changes.</summary>
	public required string? Warning { get; init; }

	/// <summary>Why the agent couldn't be asked; only the agent picker is offered then.</summary>
	public required string? Error { get; init; }
}

/// <summary>Maps a provider's advertised controls onto the inference settings they configure.</summary>
public static class InferenceControlAxes {
	/// <summary>Whether <paramref name="control"/> is the Fast Mode switch an inference profile can set.</summary>
	public static bool IsFastMode(AgentControlAxis control) {
		ArgumentNullException.ThrowIfNull(control);
		return control.Id is "fast" or "fast-mode"
			&& (control.Kind == "boolean"
				|| control.Kind == "select"
					&& control.Options.Count == 2
					&& control.Options.Any(option => option.Id == "on")
					&& control.Options.Any(option => option.Id == "off"));
	}

	/// <summary>Asks the selected provider for its controls and builds the pickers; a failed ask keeps the agent picker.</summary>
	public static async Task<InferenceChoices> AskAsync(SettingsStore settings, AgentProviderRegistry providers, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(settings);
		var controls = new InferenceControls { Defaults = [], Selected = [] };
		string? error = null;
		try {
			string providerId = settings.RequireString(InferenceSettings.DefaultProvider);
			var provider = InferenceService.Resolve(providers, providerId, out string unavailable)
				?? throw new InvalidOperationException(unavailable);
			controls = await provider.ProbeInferenceControlsAsync(
				settings.RequireString(InferenceSettings.Model), ct).ConfigureAwait(false);
		} catch (Exception ex) when (ex is not OperationCanceledException) {
			error = ex.Message;
		}
		var choices = Build(settings, providers, controls);
		return error is null ? choices : choices with { Axes = [choices.Axes[0]], Warning = null, Error = error };
	}

	/// <summary>Builds the pickers from live settings and the selected provider's <paramref name="controls"/>.</summary>
	public static InferenceChoices Build(SettingsStore settings, AgentProviderRegistry providers, InferenceControls controls) {
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(providers);
		ArgumentNullException.ThrowIfNull(controls);
		var warnings = new List<string>();
		var available = providers.Providers.OfType<IAgentInferenceProvider>().Where(p => p.Info.Available).ToArray();
		string providerId = settings.RequireString(InferenceSettings.DefaultProvider);
		string offeredBy = $"isn't offered by {available.FirstOrDefault(p => p.Info.Id == providerId)?.Info.Name ?? providerId}";
		var model = controls.Defaults.FirstOrDefault(c => c.Category == "model");
		var effort = controls.Selected.FirstOrDefault(c => c.Category == "thought_level");
		var fast = controls.Selected.FirstOrDefault(IsFastMode);
		AgentControlAxis?[] axes = [
			Axis(InferenceSettings.DefaultProvider, "Agent", null, [.. available.Select(p => new AgentControlOption { Id = p.Info.Id, Label = p.Info.Name })], "isn't installed"),
			Axis(InferenceSettings.Model, "Model", model?.ValueLabel, model?.Options, offeredBy),
			Axis(InferenceSettings.Effort, "Effort", effort?.ValueLabel, effort?.Options, offeredBy),
			Axis(
				InferenceSettings.FastMode,
				"Fast mode",
				fast is null ? null : fast.Value is "true" or "on" ? "On" : "Off",
				fast is null ? null : [new() { Id = "on", Label = "On" }, new() { Id = "off", Label = "Off" }],
				offeredBy),
		];
		return new InferenceChoices {
			Axes = [.. axes.OfType<AgentControlAxis>()],
			Warning = warnings.Count == 0 ? null : string.Join(" ", warnings),
			Error = null,
		};

		// Each axis leads with "Default (Haiku 5.5)"; a configured value the agent doesn't offer stays selected and warns.
		AgentControlAxis? Axis(string key, string label, string? resolved, IReadOnlyList<AgentControlOption>? offered, string unoffered) {
			string value = settings.RequireString(key);
			string defaultId = key == InferenceSettings.FastMode ? "inherit" : "";
			if (offered is null && value == defaultId) return null;
			var options = new List<AgentControlOption>();
			if (key != InferenceSettings.DefaultProvider) {
				options.Add(new() { Id = defaultId, Label = resolved is null ? "Default" : $"Default ({resolved})" });
			}
			options.AddRange(offered ?? []);
			var current = options.FirstOrDefault(option => option.Id == value);
			if (current is null) {
				current = new AgentControlOption { Id = value, Label = value, Description = $"{value} {unoffered}" };
				options.Add(current);
				warnings.Add($"{label} '{value}' {unoffered}.");
			}
			return new AgentControlAxis {
				Id = key,
				Label = label,
				Kind = "select",
				Value = value,
				ValueLabel = current.Id == defaultId ? resolved ?? current.Label : current.Label,
				Options = options,
			};
		}
	}
}
