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

	/// <summary>The requesting user's configured Git email, or an empty string when unset.</summary>
	public required string AuthorEmail { get; init; }

	/// <summary>Up to twenty of the user's own branch names, newest commit first.</summary>
	public required IReadOnlyList<string> MyRecentBranches { get; init; }

	/// <summary>Up to twenty branch names written by other authors, empty unless the user has none of their own.</summary>
	public required IReadOnlyList<string> OtherRecentBranches { get; init; }
}

/// <summary>The structured model proposal for a branch.</summary>
public sealed record BranchNameInferenceOutput {
	/// <summary>The complete proposed branch name, including any convention-derived prefix; empty when <see cref="NeedsMoreDetail"/>.</summary>
	public required string Branch { get; init; }

	/// <summary>Whether the input is still too vague to name a branch for, leaving the caller to ask again once the user has written more.</summary>
	public required bool NeedsMoreDetail { get; init; }
}

/// <summary>The branch-name prompt and strict serialization contracts.</summary>
public static class BranchNameInference {
	private const int MaxImages = 4;
	private const string Instructions = "Infer the repository's branch-naming convention from the supplied branch "
		+ "names and propose one complete branch name for the task described by the text and attached images. "
		+ "myRecentBranches are the requesting user's own branches; otherRecentBranches is populated only when the "
		+ "user has none. Where the examples put an author segment in the name, that segment is the requesting "
		+ "user's own: write the local part of authorEmail — minus a forge no-reply address's leading numeric id and "
		+ "'+' — rather than copying another author's. Do not invent a "
		+ "ticket, team, or prefix the examples don't show. When the input names no specific task — too short, too "
		+ "vague, or an unfinished thought — set needsMoreDetail and leave branch empty rather than guessing.";

	/// <summary>The resource policy for an automatic branch-name query.</summary>
	public static InferenceQueryOptions QueryOptions { get; } = new() {
		Origin = InferenceInvocationOrigin.Automatic,
		MaxPromptBytes = 32 * 1024,
		MaxImageCount = MaxImages,
		MaxImageBytes = MaxImages * PastedImageMedia.MaxBytes,
		MaxOutputBytes = 4 * 1024,
		TimeBudget = TimeSpan.FromSeconds(24),
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
