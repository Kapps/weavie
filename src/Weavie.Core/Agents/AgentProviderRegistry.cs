namespace Weavie.Core.Agents;

/// <summary>The required agent-provider catalog shared by every host.</summary>
public sealed class AgentProviderRegistry {
	private readonly Dictionary<string, IAgentProvider> _providers = new(StringComparer.Ordinal);

	/// <summary>Registers <paramref name="provider"/>, rejecting duplicate ids.</summary>
	public void Register(IAgentProvider provider) {
		ArgumentNullException.ThrowIfNull(provider);
		if (!_providers.TryAdd(provider.Info.Id, provider)) {
			throw new InvalidOperationException($"Agent provider '{provider.Info.Id}' is already registered.");
		}
	}

	/// <summary>The registered providers, in registration order.</summary>
	public IReadOnlyList<IAgentProvider> Providers => [.. _providers.Values];

	/// <summary>Returns the provider named by <paramref name="id"/>, or fails loudly when it is missing or unavailable.</summary>
	public IAgentProvider RequireAvailable(string id) {
		ArgumentException.ThrowIfNullOrEmpty(id);
		if (!_providers.TryGetValue(id, out var provider)) {
			throw new InvalidOperationException($"Agent provider '{id}' is not registered.");
		}

		if (!provider.Info.Available) {
			throw new InvalidOperationException(
				provider.Info.UnavailableReason ?? $"Agent provider '{provider.Info.Name}' is not available.");
		}

		return provider;
	}

	/// <summary>Returns the sole provider for the Claude-only compatibility phase.</summary>
	public IAgentProvider Sole() => _providers.Count == 1
		? RequireAvailable(_providers.Keys.Single())
		: throw new InvalidOperationException(
			$"The compatibility host requires exactly one agent provider; found {_providers.Count}.");
}
