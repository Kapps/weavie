namespace Weavie.Workflows;

/// <summary>Answers prepared workflow decisions without owning routing policy.</summary>
public interface IWorkflowDecider {
	/// <summary>Answers prepared evidence; failures are returned, except cancellation.</summary>
	Task<WorkflowDecisionResult> DecideAsync(WorkflowDecisionRequest request, CancellationToken ct);
}

/// <summary>An engine-owned snapshot of a decision and its effective policy.</summary>
internal sealed record WorkflowDecisionPlan(
	string Question, IReadOnlyList<WorkflowDecisionOption> Choices,
	IReadOnlyDictionary<string, string> Outputs, string Summary,
	IReadOnlyList<string> FilePaths, string DocumentsDirectory, double MinimumConfidence);

/// <summary>An answer's label and optional criteria, without its routing destination.</summary>
public sealed record WorkflowDecisionOption(string Name, string? Description);

/// <summary>The complete text of one explicitly referenced file.</summary>
public sealed record WorkflowFileEvidence(string Path, string Text);

/// <summary>The evidence shared by a decision's choices.</summary>
public sealed record WorkflowDecisionEvidence(
	IReadOnlyDictionary<string, string> Outputs, string Summary, IReadOnlyList<WorkflowFileEvidence> Files);

/// <summary>A question, described choices, and prepared evidence; no routing policy.</summary>
public sealed record WorkflowDecisionRequest(
	string Question, IReadOnlyList<WorkflowDecisionOption> Choices, WorkflowDecisionEvidence Evidence);

/// <summary>A model answer with confidence, or the reason no answer was available.</summary>
public abstract record WorkflowDecisionResult {
	/// <summary>An answer whose membership and confidence are validated by the engine.</summary>
	public sealed record Answered(string Choice, double Confidence) : WorkflowDecisionResult;

	/// <summary>The provider declined to answer the question.</summary>
	public sealed record Refused(string Reason) : WorkflowDecisionResult;

	/// <summary>The attempt could not produce an answer.</summary>
	public sealed record Failed(string Reason) : WorkflowDecisionResult;
}
