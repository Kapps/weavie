using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Weavie.Core.Inference;

namespace Weavie.Hosting.Inference;

/// <summary>Builds the app-global stateless inference graph shared by every host platform.</summary>
public static class InferenceComposition {
	/// <summary>Creates typed inference over the installed agent providers and live settings.</summary>
	public static IInferenceService CreateDefault(SettingsStore settings, AgentProviderRegistry agentProviders) {
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(agentProviders);
		return new InferenceService(settings, agentProviders);
	}

	/// <summary>Creates a providerless graph for deterministic hosts where inference remains disabled.</summary>
	public static IInferenceService CreateDisabled(SettingsStore settings) {
		ArgumentNullException.ThrowIfNull(settings);
		return new InferenceService(settings, new AgentProviderRegistry());
	}
}
