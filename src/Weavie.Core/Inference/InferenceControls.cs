using System.Text.Json.Serialization;
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

/// <summary>Whether <see cref="InferenceControlsState.Axes"/> reflects the selected provider yet.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<InferenceControlsStatus>))]
public enum InferenceControlsStatus {
	/// <summary>The provider is being asked; axes other than the provider may be from its previous answer.</summary>
	[JsonStringEnumMemberName("probing")]
	Probing,

	/// <summary>The axes are the provider's latest answer.</summary>
	[JsonStringEnumMemberName("ready")]
	Ready,

	/// <summary>The provider couldn't be asked; <see cref="InferenceControlsState.Error"/> says why.</summary>
	[JsonStringEnumMemberName("failed")]
	Failed,
}

/// <summary>The suggestion profile pickers: each axis id is a setting key and each option id is a value for it.</summary>
public sealed record InferenceControlsState {
	/// <summary>Whether Weavie features may make suggestion queries at all.</summary>
	public required bool Enabled { get; init; }

	/// <summary>Whether suggestions may run without a direct user action.</summary>
	public required bool Automatic { get; init; }

	/// <summary>Whether the provider has answered.</summary>
	public required InferenceControlsStatus Status { get; init; }

	/// <summary>Why the provider couldn't be asked, when <see cref="Status"/> is failed.</summary>
	public required string? Error { get; init; }

	/// <summary>A configured value the provider doesn't offer, which makes every query fail until it changes.</summary>
	public required string? Warning { get; init; }

	/// <summary>The agent, model, effort, and Fast Mode pickers, in that order, omitting ones the agent lacks.</summary>
	public required IReadOnlyList<AgentControlAxis> Axes { get; init; }
}

/// <summary>Maps a provider's advertised controls onto the inference settings they configure.</summary>
public static class InferenceControlAxes {
	/// <summary>The option id meaning "leave this to the provider" for the model and effort axes.</summary>
	public const string Default = "";

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

	/// <summary>Builds the pickers from live settings and <paramref name="controls"/>, the provider's latest answer.</summary>
	public static InferenceControlsState Build(
		SettingsStore settings,
		AgentProviderRegistry providers,
		InferenceControlsStatus status,
		string? error,
		InferenceControls? controls) {
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(providers);
		var warnings = new List<string>();
		string providerId = settings.RequireString(InferenceSettings.DefaultProvider);
		var available = providers.Providers.OfType<IAgentInferenceProvider>().Where(p => p.Info.Available).ToArray();
		string providerName = available.FirstOrDefault(p => p.Info.Id == providerId)?.Info.Name ?? providerId;
		var axes = new List<AgentControlAxis> {
			Axis(
				InferenceSettings.DefaultProvider,
				"Agent",
				providerId,
				[.. available.Select(p => new AgentControlOption { Id = p.Info.Id, Label = p.Info.Name })],
				"isn't installed",
				warnings,
				null),
		};
		if (controls is not null) {
			var defaultModel = controls.Defaults.FirstOrDefault(c => c.Category == "model");
			var model = controls.Selected.FirstOrDefault(c => c.Category == "model") ?? defaultModel;
			AddSelect(axes, InferenceSettings.Model, "Model", settings, defaultModel, model, providerName, warnings);
			var effort = controls.Selected.FirstOrDefault(c => c.Category == "thought_level");
			AddSelect(axes, InferenceSettings.Effort, "Effort", settings, effort, effort, providerName, warnings);
			AddFastMode(axes, settings, controls.Selected.FirstOrDefault(IsFastMode), providerName, warnings);
		}

		return new InferenceControlsState {
			Enabled = settings.RequireBool(InferenceSettings.Enabled),
			Automatic = settings.RequireBool(InferenceSettings.AllowAutomatic),
			Status = status,
			Error = error,
			Warning = warnings.Count == 0 ? null : string.Join(" ", warnings),
			Axes = axes,
		};
	}

	private static void AddSelect(
		List<AgentControlAxis> axes,
		string key,
		string label,
		SettingsStore settings,
		AgentControlAxis? defaults,
		AgentControlAxis? offered,
		string providerName,
		List<string> warnings) {
		string value = settings.RequireString(key);
		if (offered is null && value.Length == 0) {
			return;
		}

		axes.Add(Axis(
			key,
			label,
			value,
			[DefaultOption(Default, defaults?.ValueLabel), .. offered?.Options ?? []],
			$"isn't offered by {providerName}",
			warnings,
			defaults?.ValueLabel));
	}

	private static void AddFastMode(
		List<AgentControlAxis> axes,
		SettingsStore settings,
		AgentControlAxis? offered,
		string providerName,
		List<string> warnings) {
		string value = settings.RequireString(InferenceSettings.FastMode);
		if (offered is null && value == "inherit") {
			return;
		}

		string? resolved = offered is null ? null : offered.Value is "true" or "on" ? "On" : "Off";
		AgentControlOption[] options = offered is null
			? [DefaultOption("inherit", null)]
			: [
				DefaultOption("inherit", resolved),
				new() { Id = "on", Label = "On" },
				new() { Id = "off", Label = "Off" },
			];
		axes.Add(Axis(InferenceSettings.FastMode, "Fast mode", value, options, $"isn't offered by {providerName}", warnings, resolved));
	}

	// "Default (Haiku 5.5)": what leaving the axis to the provider resolves to, which the pickers show as the value.
	private static AgentControlOption DefaultOption(string id, string? resolved) => new() {
		Id = id,
		Label = resolved is null ? "Default" : $"Default ({resolved})",
	};

	// An unoffered configured value stays visible and selected, with a warning, instead of silently disappearing.
	private static AgentControlAxis Axis(
		string key,
		string label,
		string value,
		IReadOnlyList<AgentControlOption> options,
		string unoffered,
		List<string> warnings,
		string? resolvedDefault) {
		var current = options.FirstOrDefault(option => option.Id == value);
		if (current is null) {
			current = new AgentControlOption { Id = value, Label = value, Description = $"{value} {unoffered}" };
			options = [.. options, current];
			warnings.Add($"{label} '{value}' {unoffered}.");
		}

		return new AgentControlAxis {
			Id = key,
			Label = label,
			Kind = "select",
			Value = value,
			ValueLabel = current == options[0] && resolvedDefault is not null ? resolvedDefault : current.Label,
			Options = options,
		};
	}
}
