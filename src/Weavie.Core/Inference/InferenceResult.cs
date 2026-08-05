namespace Weavie.Core.Inference;

/// <summary>A stable reason an inference attempt produced no usable typed value.</summary>
public enum InferenceFailureKind {
	/// <summary>Ad-hoc inference is disabled.</summary>
	Disabled,

	/// <summary>Automatic inference is not allowed.</summary>
	PolicyDenied,

	/// <summary>The selected provider has no usable credential or configuration.</summary>
	NotConfigured,

	/// <summary>The provider cannot serve the requested model category.</summary>
	CategoryUnavailable,

	/// <summary>The declared operation rejected the input before transmission.</summary>
	InputRejected,

	/// <summary>The single model attempt exceeded its declared time budget.</summary>
	TimedOut,

	/// <summary>The provider rejected its credential.</summary>
	AuthenticationFailed,

	/// <summary>The provider rate-limited the attempt.</summary>
	RateLimited,

	/// <summary>The provider could not complete the attempt.</summary>
	ProviderUnavailable,

	/// <summary>The model refused the request.</summary>
	Refused,

	/// <summary>The response was missing, malformed, shape-invalid, or domain-invalid.</summary>
	InvalidResponse,
}

/// <summary>Provider-reported token usage, when available.</summary>
public sealed record InferenceUsage {
	/// <summary>Input tokens.</summary>
	public required long InputTokens { get; init; }

	/// <summary>Cached input tokens.</summary>
	public required long CachedInputTokens { get; init; }

	/// <summary>Output tokens.</summary>
	public required long OutputTokens { get; init; }
}

/// <summary>Non-content observability for one inference attempt.</summary>
public sealed record InferenceReceipt {
	/// <summary>The registered operation id.</summary>
	public required string OperationId { get; init; }

	/// <summary>The selected provider id.</summary>
	public required string ProviderId { get; init; }

	/// <summary>The category requested by the feature.</summary>
	public required InferenceModelCategory Category { get; init; }

	/// <summary>The concrete model resolved privately by the provider.</summary>
	public required string ModelId { get; init; }

	/// <summary>Elapsed wall time for the attempt.</summary>
	public required TimeSpan Duration { get; init; }

	/// <summary>The provider request id, when one was returned.</summary>
	public string? RequestId { get; init; }

	/// <summary>Provider-reported token usage, when one was returned.</summary>
	public InferenceUsage? Usage { get; init; }
}

/// <summary>The exhaustive result of one inference attempt.</summary>
/// <typeparam name="T">The operation's decoded output type.</typeparam>
public abstract record InferenceResult<T>;

/// <summary>A locally validated typed inference value.</summary>
/// <typeparam name="T">The operation's decoded output type.</typeparam>
public sealed record InferenceSuccess<T> : InferenceResult<T> {
	/// <summary>The decoded and domain-validated value.</summary>
	public required T Value { get; init; }

	/// <summary>Non-content observability for the completed attempt.</summary>
	public required InferenceReceipt Receipt { get; init; }
}

/// <summary>A non-cancellation failure; the feature must execute its disabled behavior.</summary>
/// <typeparam name="T">The operation's output type.</typeparam>
public sealed record InferenceFailure<T> : InferenceResult<T> {
	/// <summary>The stable failure category.</summary>
	public required InferenceFailureKind Kind { get; init; }

	/// <summary>A sanitized diagnostic that contains no prompt, output, credential, or raw provider body.</summary>
	public required string Detail { get; init; }

	/// <summary>Attempt metadata when a provider call began.</summary>
	public InferenceReceipt? Receipt { get; init; }
}
