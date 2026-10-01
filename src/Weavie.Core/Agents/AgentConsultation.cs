namespace Weavie.Core.Agents;

/// <summary>One registered provider as another agent sees it when choosing whom to consult.</summary>
public sealed record AgentRosterEntry {
	/// <summary>The provider identity.</summary>
	public required AgentProviderInfo Provider { get; init; }

	/// <summary>Why the provider can't be consulted, or <c>null</c> when it can.</summary>
	public required string? Unconsultable { get; init; }

	/// <summary>The provider's latest model snapshot, when it is consultable.</summary>
	public required AgentModelEntry? Models { get; init; }
}

/// <summary>Resolves consult requests against the live provider catalog and records what each consult observed.</summary>
public sealed class AgentConsultation {
	private readonly AgentProviderRegistry _providers;
	private readonly AgentModelCatalog _models;

	/// <summary>Creates the service over the shared provider and model catalogs.</summary>
	public AgentConsultation(AgentProviderRegistry providers, AgentModelCatalog models) {
		ArgumentNullException.ThrowIfNull(providers);
		ArgumentNullException.ThrowIfNull(models);
		_providers = providers;
		_models = models;
	}

	/// <summary>A service with no providers, for MCP servers that never advertise the consult tools.</summary>
	public static AgentConsultation None { get; } = new(new AgentProviderRegistry(), new AgentModelCatalog(new AgentProviderRegistry()));

	/// <summary>Every registered provider, in registry order.</summary>
	public IReadOnlyList<AgentRosterEntry> Roster() => [.. _providers.Providers.Select(provider => new AgentRosterEntry {
		Provider = provider.Info,
		Unconsultable = Unconsultable(provider),
		Models = _models.Find(provider.Info.Id),
	})];

	/// <summary>Consults <paramref name="providerId"/>; every non-cancellation failure is returned as a value.</summary>
	public async Task<AgentConsultOutcome> ConsultAsync(
		string providerId,
		AgentConsultRequest request,
		CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(request);
		var provider = _providers.Providers.FirstOrDefault(candidate => candidate.Info.Id == providerId);
		string? refusal = provider is null
			? $"No agent provider '{providerId}' is registered. Call listAgents for the exact provider id."
			: Unconsultable(provider);
		if (refusal is not null || provider is not IAgentConsultProvider consultable) {
			return new AgentConsultFailure { ModelId = request.Model, Controls = [], Detail = refusal! };
		}

		var outcome = await consultable.ConsultAsync(request, ct).ConfigureAwait(false);
		if (outcome.Controls.Count > 0) _models.Observe(providerId, outcome.Controls, AgentModelSource.Consult);
		return outcome;
	}

	private static string? Unconsultable(IAgentProvider provider) =>
		!provider.Info.Available
			? provider.Info.UnavailableReason ?? $"{provider.Info.Name} is not available."
			: provider is IAgentConsultProvider
				? null
				: $"{provider.Info.Name} can't be consulted; install its ACP agent from Manage ACP Agents instead.";
}
