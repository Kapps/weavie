using Weavie.Workflows;

namespace Weavie.Core.Workflows;

/// <summary>One workflow a start from a prompt alone offers, with how its roles stand in the chosen provider's sessions.</summary>
/// <param name="Entry">The catalog entry.</param>
/// <param name="Roles">Its roles, or <see langword="null"/> for a broken workflow.</param>
public sealed record WorkflowStartOption(WorkflowCatalogEntry Entry, WorkflowRoleResolution? Roles) {
	/// <summary>Why it can't start here, or <see langword="null"/>.</summary>
	public string? Problem => Entry.Definition is null ? Entry.Error : Roles!.Refusal;
}

/// <summary>
/// What a start from a prompt alone can choose from in one provider's sessions: the workflows offered, the configured
/// default when it is one of the startable ones, and otherwise why it isn't.
/// </summary>
/// <param name="Offered">Each winning workflow that declares no inputs or is broken, in id order.</param>
/// <param name="Default">The preselected workflow id, or <see langword="null"/> for none.</param>
/// <param name="DefaultProblem">Why the configured default can't be preselected, or <see langword="null"/>.</param>
public sealed record WorkflowStartOptions(IReadOnlyList<WorkflowStartOption> Offered, string? Default, string? DefaultProblem) {
	/// <summary>The offered workflows that can start.</summary>
	public IReadOnlyList<WorkflowDefinition> Startable => [.. Offered.Where(option => option.Problem is null).Select(option => option.Entry.Definition!)];

	/// <summary>Reads <paramref name="catalog"/> through <paramref name="roles"/> against the configured <paramref name="defaultId"/> (empty for none).</summary>
	public static WorkflowStartOptions From(WorkflowCatalog catalog, string defaultId, WorkflowRoleResolver roles) {
		ArgumentNullException.ThrowIfNull(catalog);
		ArgumentNullException.ThrowIfNull(defaultId);
		ArgumentNullException.ThrowIfNull(roles);
		var entries = catalog.List();
		IReadOnlyList<WorkflowStartOption> offered = [.. entries.Where(entry => entry.OfferedForPrompt)
			.Select(entry => new WorkflowStartOption(entry, entry.Definition is { } definition ? roles.Read(definition) : null))];
		if (defaultId.Length == 0) {
			return new(offered, null, null);
		}

		string? problem = entries.FirstOrDefault(entry => entry.ShadowedBy is null && entry.Id == defaultId) switch {
			null => "isn't a workflow here",
			{ Definition: null } broken => $"is invalid: {broken.Error}",
			{ OfferedForPrompt: false } => "declares inputs, so a prompt alone can't start it",
			_ => offered.Single(option => option.Entry.Id == defaultId).Problem is { } reason ? $"can't run here: {reason.TrimEnd('.')}" : null,
		};
		return problem is null
			? new(offered, defaultId, null)
			: new(offered, null, $"The default workflow \"{defaultId}\" {problem}.");
	}
}
