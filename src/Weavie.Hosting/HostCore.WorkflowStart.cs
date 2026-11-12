using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Weavie.Core.Inference;
using Weavie.Core.Workflows;
using Weavie.Workflows;

namespace Weavie.Hosting;

// Starting a workflow from the new-session composer: the workflows it offers in the chosen provider's sessions, the
// roles they still need a model for, and the suggestions, all read from the same catalog a start resolves. See
// docs/specs/new-session-workflow-picker/design.md and docs/specs/workflow-roles/design.md.
public sealed partial class HostCore {
	private const string SourceGoneMessage = "The source session no longer exists.";

	// Where a start reads its workflows and conventions from: the session it is made from, else the workspace. It is
	// read before any worktree exists, so a bad start leaves no session behind and the new one runs what was checked.
	private string LaunchRoot(SessionSlot? source) => source?.WorktreePath ?? WorkspaceRoot;

	// False when the session a start names is gone.
	private bool TryLaunchRoot(string? sourceId, out string root) {
		var source = string.IsNullOrWhiteSpace(sourceId) ? null : _sessions?.Find(sourceId);
		root = LaunchRoot(source);
		return source is not null || string.IsNullOrWhiteSpace(sourceId);
	}

	// The one rule for a workflow's roles in sessions of the provider described by info.
	private WorkflowRoleResolver RoleResolver(AgentProviderInfo info) => new(_workflowRoles, info, _agentModels);

	private AgentProviderInfo RequireProvider(string providerId) => WeavieWorkflows.RequireProvider(_agentProviders, providerId);

	private Task<WorkflowStartOptions> WorkflowStartOptionsAsync(string root, string providerId, CancellationToken ct) {
		string configured = _settings.RequireString(WorkflowSettings.Default, WorkspaceRoot);
		var roles = RoleResolver(RequireProvider(providerId));
		return Task.Run(() => WorkflowStartOptions.From(WeavieWorkflows.Catalog(root), configured, roles), ct);
	}

	private string RequireLaunchRoot(string? sourceId) =>
		TryLaunchRoot(sourceId, out string root) ? root : throw new InvalidOperationException(SourceGoneMessage);

	private async Task<WorkflowStartCatalog> WorkflowStartCatalogAsync(WorkflowStartTarget target, CancellationToken ct) {
		var options = await WorkflowStartOptionsAsync(RequireLaunchRoot(target.SourceId), target.ProviderId, ct).ConfigureAwait(false);
		return new(
			[.. options.Offered.Select(option => new WorkflowOptionWire(
				option.Entry.Id,
				option.Entry.Definition?.Description ?? "",
				option.Problem,
				option.Problem is null
					? [.. option.Roles!.Unfilled.Select(gap => new WorkflowRoleNeedWire(
						gap.Role, gap.MissingModel is { } missing ? WorkflowAgentControls.Stale(missing) : null))]
					: []))],
			options.Default,
			options.DefaultProblem);
	}

	private async Task<WorkflowSuggestionResult> SuggestWorkflowAsync(WorkflowSuggestionRequest request, CancellationToken ct) {
		string root = RequireLaunchRoot(request.SourceId);
		var options = await WorkflowStartOptionsAsync(root, request.ProviderId, ct).ConfigureAwait(false);
		var suggestion = await new WorkflowSuggester(_decisions, _decisionHistory)
			.SuggestAsync(options.Startable, request.Prompt, root, _settings.RequireNumber(WorkflowSettings.DecisionConfidence),
				request.UserInitiated ? InferenceInvocationOrigin.UserInitiated : InferenceInvocationOrigin.Automatic, ct)
			.ConfigureAwait(false);
		return Reply(suggestion, static (applies, workflow, error) => new WorkflowSuggestionResult(applies, workflow, error));
	}

	// The composer's reply to a suggestion: whether it applies, its choice, and why it failed.
	private static T Reply<T>(WorkflowSuggestion suggestion, Func<bool, string?, string?, T> reply) => suggestion switch {
		WorkflowSuggestion.Chosen chosen => reply(true, chosen.Choice, null),
		WorkflowSuggestion.Failed failed => reply(false, null, failed.Reason),
		_ => reply(false, null, null),
	};

	// A role's pickers for one provider: its models, answered once they aren't being probed, then what the chosen
	// model offers, asked of the agent.
	private async Task<WorkflowRoleControls> RoleControlsAsync(RoleControlsRequest request, CancellationToken ct) {
		var info = RequireProvider(request.ProviderId);
		var entry = await _agentModels.SettledAsync(info.Id, ct).ConfigureAwait(false);
		return entry is { Status: AgentModelStatus.Ready, Models: var models }
			? await WorkflowRoleAxes.AskAsync(
				info, _agentProviders.Providers.FirstOrDefault(p => p.Info.Id == info.Id) as IInferenceProvider, models, request.Model, ct)
				.ConfigureAwait(false)
			: new([], entry?.Error ?? $"{info.Name} offers no model to choose.");
	}

	private async Task<RoleSuggestionResult> SuggestRoleAsync(RoleSuggestionRequest request, CancellationToken ct) {
		string root = RequireLaunchRoot(request.SourceId);
		var resolver = RoleResolver(RequireProvider(request.ProviderId));
		var definition = await Task.Run(() => WeavieWorkflows.Catalog(root).Winner(request.Workflow)?.Definition, ct).ConfigureAwait(false);
		var read = definition is null ? null : resolver.Read(definition);
		// Only a role with nothing saved is suggested; a saved model that has gone is the user's to replace.
		if (read is not { Refusal: null } || read.Unfilled.FirstOrDefault(gap => gap.Role == request.Role) is not { MissingModel: null }) {
			return new(false, null, null);
		}
		var suggestion = await new WorkflowSuggester(_decisions, _decisionHistory).SuggestRoleAsync(
			definition!, request.Role, read.Offered, root, _settings.RequireNumber(WorkflowSettings.DecisionConfidence), ct)
			.ConfigureAwait(false);
		return Reply(suggestion, static (applies, model, error) => new RoleSuggestionResult(applies, model, error));
	}

	internal sealed record WorkflowStartTarget(string? SourceId, string ProviderId);

	internal sealed record WorkflowSuggestionRequest(string? SourceId, string ProviderId, string Prompt, bool UserInitiated);

	internal sealed record WorkflowRoleNeedWire(string Role, string? Problem);

	internal sealed record WorkflowOptionWire(string Id, string Description, string? Error, IReadOnlyList<WorkflowRoleNeedWire> Roles);

	internal sealed record WorkflowStartCatalog(IReadOnlyList<WorkflowOptionWire> Workflows, string? Default, string? DefaultProblem);

	internal sealed record WorkflowSuggestionResult(bool Applies, string? Workflow, string? Error);

	internal sealed record RoleControlsRequest(string ProviderId, string Model);

	internal sealed record RoleSuggestionRequest(string? SourceId, string ProviderId, string Workflow, string Role);

	internal sealed record RoleSuggestionResult(bool Applies, string? Model, string? Error);
}
