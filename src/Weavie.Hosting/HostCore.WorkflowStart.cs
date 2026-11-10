using Weavie.Core.Configuration;
using Weavie.Core.Workflows;
using Weavie.Workflows;

namespace Weavie.Hosting;

// Starting a workflow from the new-session composer: the workflows it offers and the one a prompt suggests, both
// read from the same catalog a start resolves. See docs/specs/new-session-workflow-picker/design.md.
public sealed partial class HostCore {
	private const string SourceGoneMessage = "The source session no longer exists.";

	// The workflows a new session can start, read from where the user starts it, before any worktree exists: an
	// unknown workflow or bad inputs must not leave a session behind, and the new session runs exactly what was checked.
	private WorkflowCatalog LaunchCatalog(SessionSlot? source) => WeavieWorkflows.Catalog(LaunchRoot(source));

	private string LaunchRoot(SessionSlot? source) => source?.WorktreePath ?? WorkspaceRoot;

	// The session a start is made from, or null for the workspace; false when the named session is gone.
	private bool TrySourceSlot(string? sourceId, out SessionSlot? source) {
		source = SourceSlot(sourceId);
		return source is not null || string.IsNullOrWhiteSpace(sourceId);
	}

	private Task<WorkflowStartOptions> WorkflowStartOptionsAsync(string root, CancellationToken ct) {
		string configured = _settings.RequireString(WorkflowSettings.Default, WorkspaceRoot);
		return Task.Run(() => WorkflowStartOptions.From(WeavieWorkflows.Catalog(root), configured), ct);
	}

	private string RequireLaunchRoot(string? sourceId) =>
		TrySourceSlot(sourceId, out var source) ? LaunchRoot(source) : throw new InvalidOperationException(SourceGoneMessage);

	private async Task<WorkflowStartCatalog> WorkflowStartCatalogAsync(WorkflowStartTarget target, CancellationToken ct) {
		var options = await WorkflowStartOptionsAsync(RequireLaunchRoot(target.SourceId), ct).ConfigureAwait(false);
		return new(
			[.. options.Offered.Select(entry => new WorkflowOptionWire(
				entry.Id, entry.Definition?.Description ?? "", entry.Definition is null ? entry.Error : null))],
			options.Default,
			options.DefaultProblem);
	}

	private async Task<WorkflowSuggestionResult> SuggestWorkflowAsync(WorkflowSuggestionRequest request, CancellationToken ct) {
		double confidence = _settings.RequireNumber(WorkflowSettings.DecisionConfidence);
		string root = RequireLaunchRoot(request.SourceId);
		var options = await WorkflowStartOptionsAsync(root, ct).ConfigureAwait(false);
		var suggestion = await new WorkflowSuggester(_decisions, _decisionHistory)
			.SuggestAsync(options.Startable, request.Prompt, root, confidence, ct).ConfigureAwait(false);
		return suggestion switch {
			WorkflowSuggestion.Chosen chosen => new(true, chosen.WorkflowId, null),
			WorkflowSuggestion.Failed failed => new(false, null, failed.Reason),
			_ => new(false, null, null),
		};
	}

	internal sealed record WorkflowStartTarget(string? SourceId);

	internal sealed record WorkflowSuggestionRequest(string? SourceId, string Prompt);

	internal sealed record WorkflowOptionWire(string Id, string Description, string? Error);

	internal sealed record WorkflowStartCatalog(IReadOnlyList<WorkflowOptionWire> Workflows, string? Default, string? DefaultProblem);

	internal sealed record WorkflowSuggestionResult(bool Applies, string? Workflow, string? Error);
}
