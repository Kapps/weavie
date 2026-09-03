namespace Weavie.Core.Inference;

/// <summary>Stable metadata for an isolated, stateless inference facet.</summary>
public sealed record InferenceProviderInfo {
	/// <summary>The model categories this provider maps explicitly.</summary>
	public required IReadOnlyList<InferenceModelCategory> Categories { get; init; }
}

/// <summary>How a configured inference profile treats the provider's Fast Mode control.</summary>
public enum InferenceFastMode {
	/// <summary>Leaves the provider's Fast Mode state unchanged.</summary>
	Inherit,

	/// <summary>Enables Fast Mode.</summary>
	On,

	/// <summary>Disables Fast Mode.</summary>
	Off,
}

/// <summary>The provider-neutral profile applied before one isolated query.</summary>
public sealed record InferenceProviderProfile {
	/// <summary>The provider-native model id, or empty to use the provider/category default.</summary>
	public required string Model { get; init; }

	/// <summary>The provider-native reasoning-effort id, or empty to use the provider/category default.</summary>
	public required string Effort { get; init; }

	/// <summary>Whether to inherit, enable, or disable the provider's Fast Mode control.</summary>
	public required InferenceFastMode FastMode { get; init; }
}

/// <summary>A serialized, schema-constrained query passed to a provider adapter.</summary>
public sealed record InferenceProviderRequest {
	/// <summary>The requested provider-neutral model category.</summary>
	public required InferenceModelCategory Category { get; init; }

	/// <summary>The live configured profile to apply before the query.</summary>
	public required InferenceProviderProfile Profile { get; init; }

	/// <summary>The owning worktree root the query runs in.</summary>
	public required string Workspace { get; init; }

	/// <summary>The complete provider-agnostic prompt.</summary>
	public required string Prompt { get; init; }

	/// <summary>The exact images supplied as provider-native content beside <see cref="Prompt"/>.</summary>
	public required IReadOnlyList<InferenceInputImage> Images { get; init; }

	/// <summary>The strict JSON schema generated from the output type.</summary>
	public required string OutputSchemaJson { get; init; }

	/// <summary>The maximum UTF-8 bytes accepted from the model's structured result.</summary>
	public required int MaxOutputBytes { get; init; }
}

/// <summary>A provider response containing either structured JSON or a stable failure.</summary>
public abstract record InferenceProviderResult {
	/// <summary>The provider-private concrete model id selected for the category.</summary>
	public required string ModelId { get; init; }

	/// <summary>The upstream request id, when available.</summary>
	public string? RequestId { get; init; }

	/// <summary>Provider-reported usage, when available.</summary>
	public InferenceUsage? Usage { get; init; }
}

/// <summary>A structured JSON response from the provider.</summary>
public sealed record InferenceProviderSuccess : InferenceProviderResult {
	/// <summary>The exact JSON text to decode locally.</summary>
	public required string OutputJson { get; init; }
}

/// <summary>An expected provider failure represented without raw provider content.</summary>
public sealed record InferenceProviderFailure : InferenceProviderResult {
	/// <summary>The stable failure category.</summary>
	public required InferenceFailureKind Kind { get; init; }

	/// <summary>A sanitized diagnostic.</summary>
	public required string Detail { get; init; }
}

/// <summary>A stateless provider adapter for one bounded structured query.</summary>
public interface IInferenceProvider {
	/// <summary>The provider's identity and explicit category coverage.</summary>
	InferenceProviderInfo InferenceInfo { get; }

	/// <summary>Runs exactly one attempt. Implementations never retry or escalate categories.</summary>
	Task<InferenceProviderResult> QueryInferenceAsync(InferenceProviderRequest request, CancellationToken ct);
}
