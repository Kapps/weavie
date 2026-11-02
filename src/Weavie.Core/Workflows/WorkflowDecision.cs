using System.Text.Json.Serialization;
using Weavie.Core.Inference;

namespace Weavie.Core.Workflows;

/// <summary>Answers a workflow's typed decisions.</summary>
public interface IWorkflowDecider {
	/// <summary>Answers <paramref name="request"/>; failures are returned, never thrown, except cancellation.</summary>
	Task<WorkflowDecisionResult> DecideAsync(WorkflowDecisionRequest request, CancellationToken ct);
}

/// <summary>The input a decision query sends the model.</summary>
public sealed record WorkflowDecisionInput {
	/// <summary>The rendered question.</summary>
	public required string Question { get; init; }

	/// <summary>The allowed answers, including <see cref="WorkflowDecision.Unsure"/>.</summary>
	public required IReadOnlyList<string> Choices { get; init; }

	/// <summary>The outputs of the stage the decision follows.</summary>
	public required IReadOnlyDictionary<string, string> Outputs { get; init; }
}

/// <summary>The model's structured answer.</summary>
public sealed record WorkflowDecisionOutput {
	/// <summary>Exactly one of the input's choices.</summary>
	public required string Choice { get; init; }
}

/// <summary>The decision prompt and its strict contracts.</summary>
public static class WorkflowDecision {
	/// <summary>The answer every decision may give; it pauses the run for the user.</summary>
	public const string Unsure = "unsure";

	private const string Instructions = "You decide one step of a software workflow. Answer the question with exactly "
		+ "one of the listed choices, judging only from the question and the supplied outputs. Answer \"unsure\" "
		+ "whenever the information given doesn't clearly support one choice; a person will decide instead.";

	/// <summary>The resource policy for one decision.</summary>
	public static InferenceQueryOptions QueryOptions { get; } = new() {
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
			new WorkflowDecisionInput { Question = request.Question, Choices = [.. request.Choices, Unsure], Outputs = request.Outputs },
			WorkflowDecisionJsonContext.Default.WorkflowDecisionInput);

	/// <summary>Maps a model answer to a result: a listed choice or unsure, else a failure naming it.</summary>
	public static WorkflowDecisionResult Interpret(WorkflowDecisionRequest request, string choice) =>
		choice == Unsure || request.Choices.Contains(choice)
			? WorkflowDecisionResult.Chose(choice)
			: WorkflowDecisionResult.Failed($"the model answered \"{choice}\", which isn't one of {string.Join(", ", request.Choices)}");
}

/// <summary>Decides through the live typed-inference provider on the utility model.</summary>
public sealed class InferenceWorkflowDecider(IInferenceService inference, string worktree) : IWorkflowDecider {
	/// <inheritdoc/>
	public async Task<WorkflowDecisionResult> DecideAsync(WorkflowDecisionRequest request, CancellationToken ct) {
		var result = await inference.QueryAsync(
			new InferenceOwner { Workspace = worktree },
			InferenceModelCategory.Utility,
			new InferenceInput { Prompt = WorkflowDecision.BuildPrompt(request), Images = [] },
			WorkflowDecisionJsonContext.Default.WorkflowDecisionOutput,
			WorkflowDecision.QueryOptions,
			ct).ConfigureAwait(false);
		return result switch {
			InferenceSuccess<WorkflowDecisionOutput> success => WorkflowDecision.Interpret(request, success.Value.Choice),
			InferenceFailure<WorkflowDecisionOutput> failure => WorkflowDecisionResult.Failed($"{failure.Kind}: {failure.Detail}"),
			_ => throw new InvalidOperationException("Unknown inference result."),
		};
	}
}

[JsonSourceGenerationOptions(
	PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
	UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
	RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(WorkflowDecisionInput))]
[JsonSerializable(typeof(WorkflowDecisionOutput))]
internal sealed partial class WorkflowDecisionJsonContext : JsonSerializerContext;
