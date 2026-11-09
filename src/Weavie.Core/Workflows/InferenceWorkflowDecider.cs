using System.Text.Json.Serialization;
using Weavie.Core.Inference;
using Weavie.Workflows;

namespace Weavie.Core.Workflows;

/// <summary>Decides through the live typed-inference provider on the utility model.</summary>
public sealed partial class InferenceWorkflowDecider(IInferenceService inference, string worktree) : IWorkflowDecider {
	private const string Instructions = "You decide one step of a software workflow. Answer with exactly one supplied choice label "
		+ "and numeric confidence between 0 and 1, estimated from the question and supplied evidence. "
		+ "Use each choice's description to distinguish its criteria. Report low confidence when evidence is insufficient. "
		+ "Return only the requested JSON; no explanations or tools.";

	private static readonly InferenceQueryOptions QueryOptions = new() {
		Origin = InferenceInvocationOrigin.UserInitiated,
		MaxImageCount = 0,
		MaxImageBytes = 0,
		MaxOutputBytes = 1024,
		TimeBudget = TimeSpan.FromSeconds(60),
	};

	private static readonly InferenceResponseType<DecisionOutput> ResponseType =
		InferenceResponseType.Of(DecisionJsonContext.Default.DecisionOutput);

	private static string BuildPrompt(WorkflowDecisionRequest request) =>
		InferencePrompts.WithJsonInput(
			Instructions,
			request,
			DecisionJsonContext.Default.WorkflowDecisionRequest);

	/// <inheritdoc/>
	public async Task<WorkflowDecisionResult> DecideAsync(WorkflowDecisionRequest request, CancellationToken ct) {
		var result = await inference.QueryAsync(
			new InferenceOwner { Workspace = worktree },
			InferenceModelCategory.Utility,
			new InferenceInput { Prompt = BuildPrompt(request), Images = [] },
			ResponseType,
			QueryOptions,
			ct).ConfigureAwait(false);
		return result switch {
			InferenceSuccess<DecisionOutput> success => new WorkflowDecisionResult.Answered(success.Value.Choice, success.Value.Confidence),
			InferenceFailure<DecisionOutput> { Kind: InferenceFailureKind.Refused } refusal => new WorkflowDecisionResult.Refused(refusal.Detail),
			InferenceFailure<DecisionOutput> failure => new WorkflowDecisionResult.Failed($"{failure.Kind}: {failure.Detail}"),
			_ => throw new InvalidOperationException("Unknown inference result."),
		};
	}

	internal sealed record DecisionOutput {
		public required string Choice { get; init; }

		public required double Confidence { get; init; }
	}

	[JsonSourceGenerationOptions(
		PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		RespectRequiredConstructorParameters = true)]
	[JsonSerializable(typeof(WorkflowDecisionRequest))]
	[JsonSerializable(typeof(DecisionOutput))]
	internal sealed partial class DecisionJsonContext : JsonSerializerContext;
}
