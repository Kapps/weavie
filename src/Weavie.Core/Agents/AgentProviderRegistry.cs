namespace Weavie.Core.Agents;

/// <summary>The required agent-provider catalog shared by every host.</summary>
public sealed class AgentProviderRegistry {
	private readonly Lock _gate = new();
	private readonly OrderedDictionary<string, IAgentProvider> _providers = new(StringComparer.Ordinal);

	/// <summary>Raised after the provider catalog is atomically replaced.</summary>
	public event Action? Changed;

	/// <summary>Registers <paramref name="provider"/>, rejecting duplicate ids.</summary>
	public void Register(IAgentProvider provider) {
		ArgumentNullException.ThrowIfNull(provider);
		lock (_gate) {
			if (!_providers.TryAdd(provider.Info.Id, provider)) {
				throw new InvalidOperationException($"Agent provider '{provider.Info.Id}' is already registered.");
			}
		}
	}

	/// <summary>Replaces the complete provider catalog in one observable operation.</summary>
	public void ReplaceAll(IEnumerable<IAgentProvider> providers) {
		ArgumentNullException.ThrowIfNull(providers);
		var replacement = new OrderedDictionary<string, IAgentProvider>(StringComparer.Ordinal);
		foreach (var provider in providers) {
			ArgumentNullException.ThrowIfNull(provider);
			if (!replacement.TryAdd(provider.Info.Id, provider)) {
				throw new InvalidOperationException($"Agent provider '{provider.Info.Id}' is registered twice.");
			}
		}
		lock (_gate) {
			_providers.Clear();
			foreach (var entry in replacement) _providers.Add(entry.Key, entry.Value);
		}
		Changed?.Invoke();
	}

	/// <summary>The registered providers, in registration order.</summary>
	public IReadOnlyList<IAgentProvider> Providers {
		get {
			lock (_gate) return [.. _providers.Values];
		}
	}

	/// <summary>Returns metadata for a registered provider, or <c>null</c> for a stale persisted id.</summary>
	public AgentProviderInfo? FindInfo(string id) {
		ArgumentException.ThrowIfNullOrEmpty(id);
		lock (_gate) return _providers.TryGetValue(id, out var provider) ? provider.Info : null;
	}

	/// <summary>Returns the provider named by <paramref name="id"/>, or fails loudly when it is missing or unavailable.</summary>
	public IAgentProvider RequireAvailable(string id) {
		ArgumentException.ThrowIfNullOrEmpty(id);
		IAgentProvider provider;
		lock (_gate) if (!_providers.TryGetValue(id, out provider!)) {
			throw new InvalidOperationException($"Agent provider '{id}' is not registered.");
		}

		if (!provider.Info.Available) {
			throw new InvalidOperationException(
				provider.Info.UnavailableReason ?? $"Agent provider '{provider.Info.Name}' is not available.");
		}

		return provider;
	}

}
