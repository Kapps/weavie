using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Weavie.Core.Editor;

namespace Weavie.Core.Inference;

/// <summary>The bounded repository context used to propose one branch name.</summary>
public sealed record BranchNameInferenceInput {
	/// <summary>The first prompt that describes the task, or empty when attached images contain the task.</summary>
	public required string Prompt { get; init; }

	/// <summary>The branch checked out in the session that requested the new worktree, or an empty string.</summary>
	public required string CurrentBranch { get; init; }

	/// <summary>Up to twenty local branch names, newest commit first.</summary>
	public required IReadOnlyList<string> RecentBranches { get; init; }
}

/// <summary>The structured model proposal for a branch.</summary>
public sealed record BranchNameInferenceOutput {
	/// <summary>The complete proposed branch name, including any convention-derived prefix.</summary>
	public required string Branch { get; init; }
}

/// <summary>The branch-name prompt and strict serialization contracts.</summary>
public static class BranchNameInference {
	private const int MaxImages = 4;
	private const string Instructions = "Infer the repository's branch-naming convention from the supplied branch "
		+ "names and propose one complete branch name for the task described by the text and attached images. Do not "
		+ "invent a ticket, username, team, or prefix unsupported by the examples.";

	/// <summary>The resource policy for an automatic branch-name query.</summary>
	public static InferenceQueryOptions QueryOptions { get; } = new() {
		Origin = InferenceInvocationOrigin.Automatic,
		MaxPromptBytes = 32 * 1024,
		MaxImageCount = MaxImages,
		MaxImageBytes = MaxImages * PastedImageMedia.MaxBytes,
		MaxOutputBytes = 4 * 1024,
		TimeBudget = TimeSpan.FromSeconds(8),
	};

	/// <summary>The strict branch-name response shape.</summary>
	public static JsonTypeInfo<BranchNameInferenceOutput> ResponseType =>
		BranchNameInferenceJsonContext.Default.BranchNameInferenceOutput;

	/// <summary>Builds the complete provider-agnostic prompt for repository context.</summary>
	public static string BuildPrompt(BranchNameInferenceInput input) =>
		InferencePrompts.WithJsonInput(
			Instructions,
			input,
			BranchNameInferenceJsonContext.Default.BranchNameInferenceInput);
}

[JsonSourceGenerationOptions(
	PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
	UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
	RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(BranchNameInferenceInput))]
[JsonSerializable(typeof(BranchNameInferenceOutput))]
internal sealed partial class BranchNameInferenceJsonContext : JsonSerializerContext;
