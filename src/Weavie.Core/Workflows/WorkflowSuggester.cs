using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.Agents;
using Weavie.Core.Inference;
using Weavie.Core.Inference.Decisions;
using Weavie.Workflows;

namespace Weavie.Core.Workflows;

/// <summary>What the decision provider suggested for the composer: a workflow, or a role's model.</summary>
public abstract record WorkflowSuggestion {
	/// <summary>It is confident: <paramref name="Choice"/>, or <see langword="null"/> for no workflow.</summary>
	public sealed record Chosen(string? Choice) : WorkflowSuggestion;

	/// <summary>Nothing to apply: it wasn't confident, nothing can start, or inference is off.</summary>
	public sealed record Unchanged : WorkflowSuggestion;

	/// <summary>The decision failed for <paramref name="Reason"/>.</summary>
	public sealed record Failed(string Reason) : WorkflowSuggestion;
}

/// <summary>
/// Suggests the workflow a prompt should start, as one decision over the startable workflows, and the model a role
/// should take, as one decision over the provider's models.
/// </summary>
public sealed partial class WorkflowSuggester(IDecisionService decisions, WorkflowDecisionHistory history) {
	/// <summary>The label for starting no workflow; its underscore keeps it from ever being a workflow id.</summary>
	public const string NoWorkflow = "no_workflow";

	private const string Instructions = "Which workflow should run the request in the prompt? Choose "
		+ NoWorkflow + " unless one workflow's description clearly fits the request.";

	private const string RoleInstructions = "Which model should fill the role in the evidence, judging by the work of the stages it runs?";

	private static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(24);

	/// <summary>
	/// Asks which of <paramref name="startable"/> fits <paramref name="prompt"/>, for the session that would start
	/// from <paramref name="worktree"/>; an answer below <paramref name="minimumConfidence"/> changes nothing.
	/// </summary>
	public async Task<WorkflowSuggestion> SuggestAsync(
		IReadOnlyList<WorkflowDefinition> startable, string prompt, string worktree, double minimumConfidence, InferenceInvocationOrigin origin, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(startable);
		if (startable.Count == 0) {
			return new WorkflowSuggestion.Unchanged();
		}

		var suggestion = await AskAsync(decisions, history, Request(worktree, origin,
			JsonSerializer.SerializeToElement(new SuggestionEvidence(prompt), SuggestionJson.Default.SuggestionEvidence),
			new(Instructions, [
				new(NoWorkflow, "No listed workflow clearly fits; the session starts with the prompt alone."),
				.. startable.Select(workflow => new DecisionChoice(workflow.Id, workflow.Description)),
			])), minimumConfidence, ct).ConfigureAwait(false);
		return suggestion is WorkflowSuggestion.Chosen { Choice: NoWorkflow } ? new WorkflowSuggestion.Chosen(null) : suggestion;
	}

	/// <summary>
	/// Asks which of <paramref name="offered"/> should fill <paramref name="role"/>, judged by the stages of
	/// <paramref name="definition"/> that run on it; with fewer than two models there is nothing to ask.
	/// </summary>
	public async Task<WorkflowSuggestion> SuggestRoleAsync(
		WorkflowDefinition definition, string role, IReadOnlyList<AgentControlOption>? offered, string worktree, double minimumConfidence,
		CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(definition);
		if (offered is not { Count: >= 2 }) {
			return new WorkflowSuggestion.Unchanged();
		}

		IReadOnlyList<string> prompts = [.. definition.Stages.Where(stage => stage.Run.Role == role)
			.Select(stage => stage.Run.Prompt?.Source ?? $"/{stage.Run.Skill} {stage.Run.Args?.Source}".TrimEnd())];
		return await AskAsync(decisions, history, Request(worktree, InferenceInvocationOrigin.Automatic,
			JsonSerializer.SerializeToElement(new RoleEvidence(role, prompts), SuggestionJson.Default.RoleEvidence),
			new(RoleInstructions, [.. offered.Select(model => new DecisionChoice(
				model.Id, model.Description is { } description ? $"{model.Label}: {description}" : model.Label))])),
			minimumConfidence, ct).ConfigureAwait(false);
	}

	/// <summary>A composer decision for the session that would start from <paramref name="worktree"/>.</summary>
	internal static DecisionRequest Request(string worktree, InferenceInvocationOrigin origin, JsonElement evidence, DecisionQuestion question) => new() {
		Owner = new InferenceOwner { Workspace = worktree },
		Evidence = evidence,
		Question = question,
		Origin = origin,
		TimeBudget = TimeBudget,
	};

	/// <summary>Asks <paramref name="request"/>; an answer below <paramref name="minimumConfidence"/> changes nothing.</summary>
	internal static async Task<WorkflowSuggestion> AskAsync(
		IDecisionService decisions, WorkflowDecisionHistory history, DecisionRequest request, double minimumConfidence, CancellationToken ct) {
		switch (await decisions.DecideAsync(request, ct).ConfigureAwait(false)) {
			case InferenceSuccess<DecisionAnswer> { Value: var answer, Receipt: var receipt }:
				history.Record(receipt);
				return answer.Probability < minimumConfidence ? new WorkflowSuggestion.Unchanged() : new WorkflowSuggestion.Chosen(answer.Choice);
			// The user turned suggestions off; that is not a failed one.
			case InferenceFailure<DecisionAnswer> { Kind: InferenceFailureKind.Disabled or InferenceFailureKind.PolicyDenied }
				when request.Origin == InferenceInvocationOrigin.Automatic:
				return new WorkflowSuggestion.Unchanged();
			case InferenceFailure<DecisionAnswer> failure:
				return new WorkflowSuggestion.Failed(failure.Detail);
			default:
				throw new InvalidOperationException("Unknown decision result.");
		}
	}

	private sealed record SuggestionEvidence(string Prompt);

	private sealed record RoleEvidence(string Role, IReadOnlyList<string> Prompts);

	[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
	[JsonSerializable(typeof(SuggestionEvidence))]
	[JsonSerializable(typeof(RoleEvidence))]
	private sealed partial class SuggestionJson : JsonSerializerContext;
}
