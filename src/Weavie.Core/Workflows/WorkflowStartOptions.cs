using Weavie.Workflows;

namespace Weavie.Core.Workflows;

/// <summary>
/// What a start from a prompt alone can choose from: the workflows offered, the configured default when it is one
/// of the startable ones, and otherwise why it isn't.
/// </summary>
/// <param name="Offered">Each winning workflow that declares no inputs or is broken, in id order.</param>
/// <param name="Default">The preselected workflow id, or <see langword="null"/> for none.</param>
/// <param name="DefaultProblem">Why the configured default can't be preselected, or <see langword="null"/>.</param>
public sealed record WorkflowStartOptions(IReadOnlyList<WorkflowCatalogEntry> Offered, string? Default, string? DefaultProblem) {
	/// <summary>The offered workflows that can start.</summary>
	public IReadOnlyList<WorkflowDefinition> Startable => [.. Offered.Select(entry => entry.Definition).OfType<WorkflowDefinition>()];

	/// <summary>Reads <paramref name="catalog"/> against the configured <paramref name="defaultId"/> (empty for none).</summary>
	public static WorkflowStartOptions From(WorkflowCatalog catalog, string defaultId) {
		ArgumentNullException.ThrowIfNull(catalog);
		ArgumentNullException.ThrowIfNull(defaultId);
		var entries = catalog.List();
		IReadOnlyList<WorkflowCatalogEntry> offered = [.. entries.Where(entry => entry.OfferedForPrompt)];
		if (defaultId.Length == 0) {
			return new(offered, null, null);
		}

		string? problem = entries.FirstOrDefault(entry => entry.ShadowedBy is null && entry.Id == defaultId) switch {
			null => "isn't a workflow here",
			{ Definition: null } broken => $"is invalid: {broken.Error}",
			{ OfferedForPrompt: false } => "declares inputs, so a prompt alone can't start it",
			_ => null,
		};
		return problem is null
			? new(offered, defaultId, null)
			: new(offered, null, $"The default workflow \"{defaultId}\" {problem}.");
	}
}
