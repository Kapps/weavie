using System.Text.Json.Serialization;
using Weavie.Core.Inference;
using Weavie.Core.Inference.Decisions;
using Weavie.Workflows;

namespace Weavie.Core.Workflows;

/// <summary>Answers workflow decisions through the live decision provider, recording each answer's provider and time.</summary>
public sealed partial class ModelWorkflowDecider : IWorkflowDecider {
	private static readonly DecisionQueryOptions Options = new() {
		Origin = InferenceInvocationOrigin.UserInitiated,
		TimeBudget = TimeSpan.FromSeconds(60),
	};

	private readonly IDecisionService _decisions;
	private readonly InferenceOwner _owner;
	private readonly WorkflowDecisionHistory _history;

	/// <summary>Creates the decider for the session rooted at <paramref name="worktree"/>.</summary>
	public ModelWorkflowDecider(IDecisionService decisions, string worktree, WorkflowDecisionHistory history) {
		ArgumentNullException.ThrowIfNull(decisions);
		ArgumentException.ThrowIfNullOrWhiteSpace(worktree);
		ArgumentNullException.ThrowIfNull(history);
		_decisions = decisions;
		_owner = new InferenceOwner { Workspace = worktree };
		_history = history;
	}

	/// <inheritdoc/>
	public async Task<WorkflowDecisionResult> DecideAsync(WorkflowDecisionRequest request, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(request);
		var question = new DecisionQuestion(
			request.Question,
			[.. request.Choices.Select(choice => new DecisionChoice(choice.Name, choice.Description))]);
		var result = await _decisions.DecideAsync(
			_owner, request.Evidence, EvidenceJson.Default.WorkflowDecisionEvidence, question, Options, ct).ConfigureAwait(false);
		switch (result) {
			case DecisionAnswer answer:
				_history.Append(answer.Receipt.ProviderId, answer.Receipt.Duration);
				return new WorkflowDecisionResult.Answered(answer.Choice, answer.Probability);
			case DecisionFailure { Kind: InferenceFailureKind.Refused } refusal:
				return new WorkflowDecisionResult.Refused(refusal.Detail);
			case DecisionFailure failure:
				return new WorkflowDecisionResult.Failed($"{failure.Kind}: {failure.Detail}");
			default:
				throw new InvalidOperationException("Unknown decision result.");
		}
	}

	[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
	[JsonSerializable(typeof(WorkflowDecisionEvidence))]
	private sealed partial class EvidenceJson : JsonSerializerContext;
}
