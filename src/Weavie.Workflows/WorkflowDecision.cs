namespace Weavie.Workflows;

/// <summary>Answers a workflow's typed decisions.</summary>
public interface IWorkflowDecider {
	/// <summary>Answers <paramref name="request"/>; failures are returned, never thrown, except cancellation.</summary>
	Task<WorkflowDecisionResult> DecideAsync(WorkflowDecisionRequest request, CancellationToken ct);
}

/// <summary>The rules every decider shares.</summary>
public static class WorkflowDecision {
	/// <summary>The answer every model decision may give; it hands the question to the user.</summary>
	public const string Unsure = "unsure";

	/// <summary>The answers a model may give to <paramref name="request"/>: its choices plus <see cref="Unsure"/>.</summary>
	public static IReadOnlyList<string> ModelChoices(WorkflowDecisionRequest request) => [.. request.Choices, Unsure];

	/// <summary>Maps a model answer to a result: a listed choice or unsure, else a failure naming it.</summary>
	public static WorkflowDecisionResult Interpret(WorkflowDecisionRequest request, string choice) =>
		choice == Unsure || request.Choices.Contains(choice)
			? WorkflowDecisionResult.Chose(choice)
			: WorkflowDecisionResult.Failed($"the model answered \"{choice}\", which isn't one of {string.Join(", ", request.Choices)}");
}
