using System.Text.Json.Serialization;
using Weavie.Core.Inference;
using Weavie.Workflows;

namespace Weavie.Core.Workflows;

/// <summary>Decides through the live typed-inference provider on the utility model.</summary>
public sealed partial class InferenceWorkflowDecider(IInferenceService inference, string worktree) : IWorkflowDecider {
	private const string Instructions = "You decide one step of a software workflow. Answer the question with exactly "
		+ "one of the listed choices, judging only from the question, the supplied outputs, and the agent's summary. "
		+ "Answer \"unsure\" whenever the information given doesn't clearly support one choice; a person will decide instead.";

	private static readonly InferenceQueryOptions QueryOptions = new() {
		Origin = InferenceInvocationOrigin.UserInitiated,
		MaxPromptBytes = 64 * 1024,
		MaxImageCount = 0,
		MaxImageBytes = 0,
		MaxOutputBytes = 1024,
		TimeBudget = TimeSpan.FromSeconds(60),
	};

	/// <summary>The full prompt for <paramref name="request"/>.</summary>
	public static string BuildPrompt(WorkflowDecisionRequest request) =>
		InferencePrompts.WithJsonInput(
			Instructions,
			new DecisionInput {
				Question = request.Question,
				Choices = WorkflowDecision.ModelChoices(request),
				Outputs = request.Outputs,
				Summary = request.Summary,
			},
			DecisionJsonContext.Default.DecisionInput);

	/// <inheritdoc/>
	public async Task<WorkflowDecisionResult> DecideAsync(WorkflowDecisionRequest request, CancellationToken ct) {
		var result = await inference.QueryAsync(
			new InferenceOwner { Workspace = worktree },
			InferenceModelCategory.Utility,
			new InferenceInput { Prompt = BuildPrompt(request), Images = [] },
			DecisionJsonContext.Default.DecisionOutput,
			QueryOptions,
			ct).ConfigureAwait(false);
		return result switch {
			InferenceSuccess<DecisionOutput> success => WorkflowDecision.Interpret(request, success.Value.Choice),
			InferenceFailure<DecisionOutput> failure => WorkflowDecisionResult.Failed($"{failure.Kind}: {failure.Detail}"),
			_ => throw new InvalidOperationException("Unknown inference result."),
		};
	}

	internal sealed record DecisionInput {
		public required string Question { get; init; }

		public required IReadOnlyList<string> Choices { get; init; }

		public required IReadOnlyDictionary<string, string> Outputs { get; init; }

		public required string Summary { get; init; }
	}

	internal sealed record DecisionOutput {
		public required string Choice { get; init; }
	}

	[JsonSourceGenerationOptions(
		PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		RespectRequiredConstructorParameters = true)]
	[JsonSerializable(typeof(DecisionInput))]
	[JsonSerializable(typeof(DecisionOutput))]
	internal sealed partial class DecisionJsonContext : JsonSerializerContext;
}
