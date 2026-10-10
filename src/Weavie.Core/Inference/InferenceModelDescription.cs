using Weavie.Core.Agents;
using Weavie.Core.Configuration;

namespace Weavie.Core.Inference;

/// <summary>Tells the user which model automatic suggestions run on, so enabling them is an informed choice.</summary>
public static class InferenceModelDescription {
	/// <summary>Describes the model and effort the live inference settings select for small suggestions.</summary>
	public static string Describe(SettingsStore settings, AgentProviderRegistry agentProviders) {
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(agentProviders);
		string providerId = settings.RequireString(InferenceSettings.DefaultProvider);
		if (InferenceService.Resolve(agentProviders, providerId, out string unavailable) is not { } provider) {
			return $"Suggestions can't run: {unavailable}";
		}

		var info = provider.InferenceInfo;
		string model = settings.RequireString(InferenceSettings.Model) is { Length: > 0 } configured
			? $"'{configured}'"
			: info.UtilityModel;
		string effort = settings.RequireString(InferenceSettings.Effort) is { Length: > 0 } configuredEffort
			? configuredEffort
			: info.UtilityEffort;
		string atEffort = effort.Length > 0 ? $" at {effort} effort" : "";
		return $"Suggestions use {model}{atEffort}, independent of your chat's model; "
			+ $"change it with the {InferenceSettings.Model} setting.";
	}
}
