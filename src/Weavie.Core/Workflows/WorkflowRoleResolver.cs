using Weavie.Core.Agents;
using Weavie.Workflows;

namespace Weavie.Core.Workflows;

/// <summary>A role a session's provider has no usable agent for.</summary>
/// <param name="Role">The role name.</param>
/// <param name="MissingModel">The saved model the provider no longer offers, or <see langword="null"/> when nothing is saved.</param>
public sealed record WorkflowRoleGap(string Role, string? MissingModel);

/// <summary>How a definition's roles stand in one provider's sessions, read from one snapshot of its models.</summary>
/// <param name="Agents">The agent filling each filled role, named for it.</param>
/// <param name="Unfilled">Each role that needs a model.</param>
/// <param name="Refusal">Why the provider can't run the definition at all, or <see langword="null"/>.</param>
/// <param name="Offered">The models the provider offered then, or <see langword="null"/> while they weren't known.</param>
public sealed record WorkflowRoleResolution(
	IReadOnlyList<WorkflowAgent> Agents, IReadOnlyList<WorkflowRoleGap> Unfilled, string? Refusal, IReadOnlyList<AgentControlOption>? Offered);

/// <summary>
/// The one rule for filling a definition's roles in sessions of <paramref name="provider"/>: a saved role is used as
/// saved, never substituted; a saved model the model catalog has stopped offering is a gap, and while the catalog
/// doesn't know the provider's models a saved role is trusted until its step opens.
/// </summary>
public sealed class WorkflowRoleResolver(WorkflowRoleStore roles, AgentProviderInfo provider, AgentModelCatalog models) : IWorkflowRoleResolver {
	private static readonly Dictionary<string, WorkflowRolePick> NoPicks = new(StringComparer.Ordinal);

	/// <summary>How <paramref name="definition"/>'s roles stand.</summary>
	public WorkflowRoleResolution Read(WorkflowDefinition definition) => Read(definition, NoPicks);

	/// <inheritdoc/>
	public IReadOnlyList<WorkflowAgent> Resolve(WorkflowDefinition definition) => Resolve(definition, NoPicks);

	/// <summary>
	/// Fills roles with <paramref name="picks"/> (role to a model this provider offers and its options) over the saved
	/// ones, saving nothing until <see cref="Picks.Save"/>.
	/// </summary>
	public Picks Picking(IReadOnlyDictionary<string, WorkflowRolePick> picks) => new(this, picks);

	private WorkflowRoleResolution Read(WorkflowDefinition definition, IReadOnlyDictionary<string, WorkflowRolePick> picks) {
		ArgumentNullException.ThrowIfNull(definition);
		var entry = models.Find(provider.Id);
		var offered = entry is { Status: AgentModelStatus.Ready } ? entry.Models : null;
		List<WorkflowAgent> agents = [];
		List<WorkflowRoleGap> gaps = [];
		foreach (string role in definition.Roles) {
			var saved = picks.TryGetValue(role, out var pick) ? Agent(pick) : roles.Find(provider.Id, role);
			if (saved is null) {
				gaps.Add(new(role, null));
			} else if (saved.Provider == provider.Id && offered is not null && offered.All(option => option.Id != saved.Model)) {
				gaps.Add(new(role, saved.Model));
			} else {
				agents.Add(saved.For(role));
			}
		}
		string? refusal = WorkflowAgentControls.Refusal(definition, agents, provider, null)
			?? (gaps.Count > 0 && (entry is null || offered is { Count: 0 }) ? WorkflowAgentControls.NoModelChoice(definition, gaps[0].Role, provider) : null);
		return new(agents, gaps, refusal, offered);
	}

	// The provider's own refusal precedes the model checks, so a pick never masks why the provider can't run the definition.
	private IReadOnlyList<WorkflowAgent> Resolve(WorkflowDefinition definition, IReadOnlyDictionary<string, WorkflowRolePick> picks) {
		var read = Read(definition, picks);
		string? refusal = picks.Keys.FirstOrDefault(role => !definition.Roles.Contains(role)) is { } stray
			? $"Workflow {definition.Id} asks for no role \"{stray}\"."
			: read.Refusal
			?? (picks.Count > 0 && read.Offered is null ? $"{provider.Name}'s models aren't known yet, so its roles can't be picked."
			: picks.FirstOrDefault(pick => read.Offered!.All(option => option.Id != pick.Value.Model)) is { Key: { } role, Value: var refused }
				? $"Role \"{role}\" can't take model \"{refused.Model}\": {provider.Name} offers {string.Join(", ", read.Offered!.Select(o => o.Id))}."
			: read.Unfilled.Count > 0 ? WorkflowAgentControls.Unfilled(definition, read.Unfilled, provider, read.Offered) : null);
		return refusal is null ? read.Agents : throw new WorkflowException(refusal);
	}

	private void Save(IReadOnlyDictionary<string, WorkflowRolePick> picks) {
		if (picks.Count > 0) roles.Set(provider.Id, picks.ToDictionary(pick => pick.Key, pick => Agent(pick.Value), StringComparer.Ordinal));
	}

	// A pick replaces the role whole: its options belong to its model.
	private WorkflowRoleAgent Agent(WorkflowRolePick pick) => new(provider.Id, pick.Model, pick.Options);

	/// <summary>A start's picks: they fill its roles when it is prepared, and are saved only once the start was.</summary>
	public sealed class Picks : IWorkflowRoleResolver {
		private readonly WorkflowRoleResolver _resolver;
		private readonly IReadOnlyDictionary<string, WorkflowRolePick> _picks;
		private bool _resolved;

		internal Picks(WorkflowRoleResolver resolver, IReadOnlyDictionary<string, WorkflowRolePick> picks) {
			_resolver = resolver;
			_picks = picks;
		}

		/// <inheritdoc/>
		public IReadOnlyList<WorkflowAgent> Resolve(WorkflowDefinition definition) {
			var agents = _resolver.Resolve(definition, _picks);
			_resolved = true;
			return agents;
		}

		/// <summary>Saves every pick in one write; only after a launch was prepared with them.</summary>
		public void Save() {
			if (!_resolved) throw new InvalidOperationException("Picks are saved only after a launch was prepared with them.");
			_resolver.Save(_picks);
		}
	}
}
