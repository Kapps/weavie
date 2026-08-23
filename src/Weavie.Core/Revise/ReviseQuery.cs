using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Weavie.Core.Inference;

namespace Weavie.Core.Revise;

/// <summary>One region handed to the model.</summary>
public sealed record ReviseQueryRegion {
	/// <summary>The id the reply must echo back.</summary>
	public required int Id { get; init; }

	/// <summary>The file's path, so the model can infer the language and the repository's conventions.</summary>
	public required string Path { get; init; }

	/// <summary>The region's exact current text.</summary>
	public required string Text { get; init; }
}

/// <summary>The complete input to one batched revision query.</summary>
public sealed record ReviseQueryInput {
	/// <summary>What the caller wants done to every region.</summary>
	public required string Instruction { get; init; }

	/// <summary>The regions to revise.</summary>
	public required IReadOnlyList<ReviseQueryRegion> Regions { get; init; }
}

/// <summary>One region's replacement text.</summary>
public sealed record ReviseQueryRevision {
	/// <summary>The id of the region this replaces.</summary>
	public required int Id { get; init; }

	/// <summary>The replacement text.</summary>
	public required string Text { get; init; }
}

/// <summary>The strict revision response shape.</summary>
public sealed record ReviseQueryOutput {
	/// <summary>One entry per region the model revised.</summary>
	public required IReadOnlyList<ReviseQueryRevision> Regions { get; init; }
}

/// <summary>The revision prompt and its strict serialization contracts.</summary>
public static class ReviseQuery {
	private const string Instructions = "Apply the supplied instruction to each region's text and return the "
		+ "replacement for every region id. Preserve each region's leading indentation. Return replacement text "
		+ "only: no commentary, no markdown fences.";

	/// <summary>The strict revision response shape.</summary>
	public static JsonTypeInfo<ReviseQueryOutput> ResponseType => ReviseQueryJsonContext.Default.ReviseQueryOutput;

	/// <summary>
	/// The resource policy for one batched query. The time budget is the outer bound for a whole batch, measured
	/// against a worst observed single-call latency near 80 seconds.
	/// </summary>
	/// <param name="origin">Whether a person initiated the revision.</param>
	public static InferenceQueryOptions OptionsFor(InferenceInvocationOrigin origin) => new() {
		Origin = origin,
		MaxPromptBytes = 64 * 1024,
		MaxImageCount = 0,
		MaxImageBytes = 0,
		MaxOutputBytes = 32 * 1024,
		TimeBudget = TimeSpan.FromSeconds(120),
	};

	/// <summary>Builds the complete provider-agnostic prompt for a batch of regions.</summary>
	/// <param name="input">The instruction and the regions it applies to.</param>
	public static string BuildPrompt(ReviseQueryInput input) =>
		InferencePrompts.WithJsonInput(Instructions, input, ReviseQueryJsonContext.Default.ReviseQueryInput);
}

[JsonSourceGenerationOptions(
	PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
	UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
	RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ReviseQueryInput))]
[JsonSerializable(typeof(ReviseQueryOutput))]
internal sealed partial class ReviseQueryJsonContext : JsonSerializerContext;
