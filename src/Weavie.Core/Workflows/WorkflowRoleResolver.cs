using Weavie.Core.Agents;
using Weavie.Workflows;

namespace Weavie.Core.Workflows;

/// <summary>A role a session's provider has no usable agent for.</summary>
/// <param name="Role">The role name.</param>
/// <param name="MissingModel">The saved model the provider no longer offers, or <see langword="null"/> when nothing is saved.</param>
public sealed record WorkflowRoleGap(string Role, string? MissingModel);

/// <summary>How a definition's roles stand in one provider's sessions.</summary>
/// <param name="Agents">The agent filling each filled role, named for it.</param>
/// <param name="Unfilled">Each role that needs a model.</param>
/// <param name="Refusal">Why the provider can't run the definition at all, or <see langword="null"/>.</param>
public sealed record WorkflowRoleResolution(IReadOnlyList<WorkflowAgent> Agents, IReadOnlyList<WorkflowRoleGap> Unfilled, string? Refusal);

/// <summary>
/// The one rule for filling a definition's roles in sessions of <paramref name="provider"/>: a saved role is used as
/// saved, never substituted; a saved model the model catalog has stopped offering is a gap, and while the catalog
/// doesn't know the provider's models a saved role is trusted until its step opens.
/// </summary>
public sealed class WorkflowRoleResolver(WorkflowRoleStore roles, AgentProviderInfo provider, AgentModelCatalog models) : IWorkflowRoleResolver {
	/// <summary>The models the provider offers now, or <see langword="null"/> while they aren't known.</summary>
	public IReadOnlyList<AgentControlOption>? Offered => models.Find(provider.Id) is { Status: AgentModelStatus.Ready } entry ? entry.Models : null;

	/// <summary>How <paramref name="definition"/>'s roles stand.</summary>
	public WorkflowRoleResolution Read(WorkflowDefinition definition) {
		ArgumentNullException.ThrowIfNull(definition);
		var entry = models.Find(provider.Id);
		var offered = Offered;
		List<WorkflowAgent> agents = [];
		List<WorkflowRoleGap> gaps = [];
		foreach (string role in definition.Roles) {
			var saved = roles.Find(provider.Id, role);
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
		return new(agents, gaps, refusal);
	}

	/// <inheritdoc/>
	public IReadOnlyList<WorkflowAgent> Resolve(WorkflowDefinition definition) {
		var read = Read(definition);
		string? refusal = read.Refusal
			?? (read.Unfilled.Count > 0 ? WorkflowAgentControls.Unfilled(definition, read.Unfilled, provider, Offered) : null);
		return refusal is null ? read.Agents : throw new WorkflowException(refusal);
	}

	/// <summary>Saves <paramref name="model"/> as <paramref name="role"/> for this provider, dropping its previous options.</summary>
	public void Pick(string role, string model) {
		var offered = Offered ?? throw new WorkflowException($"Role \"{role}\" can't take model \"{model}\": {provider.Name}'s models aren't known yet.");
		if (offered.All(option => option.Id != model)) {
			throw new WorkflowException($"Role \"{role}\" can't take model \"{model}\": {provider.Name} offers {string.Join(", ", offered.Select(o => o.Id))}.");
		}
		roles.Set(provider.Id, role, new(provider.Id, model, new Dictionary<string, string>()));
	}
}
