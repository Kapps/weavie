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

	/// <summary>Returns the sole provider for the Claude-only compatibility phase.</summary>
	public IAgentProvider Sole() => _providers.Count == 1
		? _providers.Values.Single()
		: throw new InvalidOperationException(
			$"The compatibility host requires exactly one agent provider; found {_providers.Count}.");
}
