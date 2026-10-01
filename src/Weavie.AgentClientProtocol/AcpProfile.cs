using Weavie.Core.Agents;
using Weavie.Core.Inference;

namespace Weavie.AgentClientProtocol;

/// <summary>Applies explicit provider-native profile values to a transient session, failing on any unadvertised value.</summary>
internal sealed class AcpProfile(AcpTransientConnection connection, string sessionId) {
	/// <summary>Applies <paramref name="profile"/> over the controls <paramref name="setup"/> advertised.</summary>
	public static Task<IReadOnlyList<AgentControlAxis>> ApplyAsync(
		AcpTransientConnection connection,
		string sessionId,
		System.Text.Json.JsonElement setup,
		InferenceProviderProfile profile,
		CancellationToken ct) =>
		new AcpProfile(connection, sessionId).ApplyAsync(AcpConfigurationOptions.ReadIfPresent(setup), profile, ct);

	private async Task<IReadOnlyList<AgentControlAxis>> ApplyAsync(
		IReadOnlyList<AgentControlAxis> state,
		InferenceProviderProfile profile,
		CancellationToken ct) {
		if (profile.Model.Length > 0) {
			state = await SetSelectAsync(
				state, "model", "model", profile.Model, ct).ConfigureAwait(false);
		}
		if (profile.Effort.Length > 0) {
			state = await SetSelectAsync(
				state, "thought_level", "effort", profile.Effort, ct).ConfigureAwait(false);
		}
		if (profile.FastMode != InferenceFastMode.Inherit) {
			state = await SetFastModeAsync(state, profile.FastMode, ct).ConfigureAwait(false);
		}
		ValidateAppliedProfile(state, profile);
		return state;
	}

	private async Task<IReadOnlyList<AgentControlAxis>> SetSelectAsync(
		IReadOnlyList<AgentControlAxis> state,
		string category,
		string description,
		string value,
		CancellationToken ct) {
		var control = FindControl(state, description, candidate => candidate.Category == category);
		if (control.Kind != "select") {
			throw ProfileFailure($"the {description} control '{control.Id}' is not selectable");
		}
		if (control.Options.All(option => option.Id != value)) {
			throw ProfileFailure($"the {description} control '{control.Id}' does not advertise value '{value}'");
		}
		return control.Value == value
			? state
			: await SetConfigurationAsync(control, value, description, ct).ConfigureAwait(false);
	}

	private async Task<IReadOnlyList<AgentControlAxis>> SetFastModeAsync(
		IReadOnlyList<AgentControlAxis> state,
		InferenceFastMode fastMode,
		CancellationToken ct) {
		var control = FindFastControl(state);
		string value = control.Kind switch {
			"boolean" => fastMode == InferenceFastMode.On ? "true" : "false",
			"select" => fastMode == InferenceFastMode.On ? "on" : "off",
			_ => throw ProfileFailure($"the Fast Mode control '{control.Id}' has an unsupported shape"),
		};
		if (control.Options.All(option => option.Id != value)) {
			throw ProfileFailure($"the Fast Mode control '{control.Id}' does not advertise value '{value}'");
		}
		return control.Value == value
			? state
			: await SetConfigurationAsync(control, value, "Fast Mode", ct).ConfigureAwait(false);
	}

	private async Task<IReadOnlyList<AgentControlAxis>> SetConfigurationAsync(
		AgentControlAxis control,
		string value,
		string description,
		CancellationToken ct) {
		var result = await connection.RequestAsync(
			"session/set_config_option",
			AcpConfigurationOptions.SetParameters(sessionId, control, value),
			ct).ConfigureAwait(false);
		return AcpConfigurationOptions.ReadRequired(
			result,
			$"The ACP {description} response is missing configOptions for session '{sessionId}'.");
	}

	private AgentControlAxis FindControl(
		IReadOnlyList<AgentControlAxis> state,
		string description,
		Func<AgentControlAxis, bool> matches) =>
		state.FirstOrDefault(matches) ?? throw ProfileFailure($"no {description} control is advertised");

	private AgentControlAxis FindFastControl(IReadOnlyList<AgentControlAxis> state) {
		var candidates = state.Where(control =>
			(control.Id is "fast" or "fast-mode")
			&& (control.Kind == "boolean"
				|| control.Kind == "select"
					&& control.Options.Count == 2
					&& control.Options.Any(option => option.Id == "on")
					&& control.Options.Any(option => option.Id == "off")))
			.ToArray();
		return candidates.Length switch {
			0 => throw ProfileFailure("no Fast Mode control is advertised"),
			1 => candidates[0],
			_ => throw ProfileFailure("more than one Fast Mode control is advertised"),
		};
	}

	private void ValidateAppliedProfile(
		IReadOnlyList<AgentControlAxis> state,
		InferenceProviderProfile profile) {
		if (profile.Model.Length > 0) EnsureCurrentValue(state, "model", "model", profile.Model);
		if (profile.Effort.Length > 0) EnsureCurrentValue(state, "thought_level", "effort", profile.Effort);
		if (profile.FastMode == InferenceFastMode.Inherit) return;

		var control = FindFastControl(state);
		string expected = control.Kind == "boolean"
			? profile.FastMode == InferenceFastMode.On ? "true" : "false"
			: profile.FastMode == InferenceFastMode.On ? "on" : "off";
		if (control.Value != expected) {
			throw ProfileFailure("the final Fast Mode value does not match the configured value");
		}
	}

	private void EnsureCurrentValue(
		IReadOnlyList<AgentControlAxis> state,
		string category,
		string description,
		string expected) {
		var control = FindControl(state, description, candidate => candidate.Category == category);
		if (control.Value != expected) {
			throw ProfileFailure($"the final {description} value does not match '{expected}'");
		}
	}

	private AcpInferenceProfileException ProfileFailure(string detail) =>
		new($"The ACP agent '{connection.Definition.Name}' cannot apply the configured profile: {detail}.");
}

internal sealed class AcpInferenceProfileException(string message) : Exception(message);
