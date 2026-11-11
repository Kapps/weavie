using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.Inference;
using Weavie.Core.Inference.Decisions;
using Weavie.Workflows;

namespace Weavie.Core.Workflows;

/// <summary>What the decision provider made of a prompt.</summary>
public abstract record WorkflowSuggestion {
	/// <summary>It is confident: <paramref name="WorkflowId"/>, or <see langword="null"/> for no workflow.</summary>
	public sealed record Chosen(string? WorkflowId) : WorkflowSuggestion;

	/// <summary>Nothing to apply: it wasn't confident, nothing can start, or inference is off.</summary>
	public sealed record Unchanged : WorkflowSuggestion;

	/// <summary>The decision failed for <paramref name="Reason"/>.</summary>
	public sealed record Failed(string Reason) : WorkflowSuggestion;
}

/// <summary>Suggests the workflow a prompt should start, as one decision over the startable workflows.</summary>
public sealed partial class WorkflowSuggester(IDecisionService decisions, WorkflowDecisionHistory history) {
	/// <summary>The label for starting no workflow; its underscore keeps it from ever being a workflow id.</summary>
	public const string NoWorkflow = "no_workflow";

	private const string Instructions = "Which workflow should run the request in the prompt? Choose "
		+ NoWorkflow + " unless one workflow's description clearly fits the request.";

	private static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(24);

	/// <summary>
	/// Asks which of <paramref name="startable"/> fits <paramref name="prompt"/>, for the session that would start
	/// from <paramref name="worktree"/>; an answer below <paramref name="minimumConfidence"/> changes nothing.
	/// </summary>
	public async Task<WorkflowSuggestion> SuggestAsync(
		IReadOnlyList<WorkflowDefinition> startable, string prompt, string worktree, double minimumConfidence, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(startable);
		if (startable.Count == 0) {
			return new WorkflowSuggestion.Unchanged();
		}

		var result = await decisions.DecideAsync(new DecisionRequest {
			Owner = new InferenceOwner { Workspace = worktree },
			Evidence = JsonSerializer.SerializeToElement(new SuggestionEvidence(prompt), SuggestionJson.Default.SuggestionEvidence),
			Question = new(Instructions, [
				new(NoWorkflow, "No listed workflow clearly fits; the session starts with the prompt alone."),
				.. startable.Select(workflow => new DecisionChoice(workflow.Id, workflow.Description)),
			]),
			Origin = InferenceInvocationOrigin.Automatic,
			TimeBudget = TimeBudget,
		}, ct).ConfigureAwait(false);
		switch (result) {
			case InferenceSuccess<DecisionAnswer> { Value: var answer, Receipt: var receipt }:
				history.Record(receipt);
				return answer.Probability < minimumConfidence
					? new WorkflowSuggestion.Unchanged()
					: new WorkflowSuggestion.Chosen(answer.Choice == NoWorkflow ? null : answer.Choice);
			// The user turned suggestions off; that is not a failed one.
			case InferenceFailure<DecisionAnswer> { Kind: InferenceFailureKind.Disabled or InferenceFailureKind.PolicyDenied }:
				return new WorkflowSuggestion.Unchanged();
			case InferenceFailure<DecisionAnswer> failure:
				return new WorkflowSuggestion.Failed(failure.Detail);
			default:
				throw new InvalidOperationException("Unknown decision result.");
		}
	}

	private sealed record SuggestionEvidence(string Prompt);

	[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
	[JsonSerializable(typeof(SuggestionEvidence))]
	private sealed partial class SuggestionJson : JsonSerializerContext;
}
