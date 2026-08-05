using System.Text.Json.Serialization.Metadata;

namespace Weavie.Core.Inference;

/// <summary>The non-generic metadata every registered typed inference operation exposes.</summary>
public interface IInferenceOperation {
	/// <summary>The stable operation id used in policy, receipts, and provider schema names.</summary>
	string Id { get; }

	/// <summary>The fixed, code-reviewed instructions sent to the model.</summary>
	string Instructions { get; }

	/// <summary>The model categories this operation permits its caller to select.</summary>
	IReadOnlyList<InferenceModelCategory> AllowedCategories { get; }

	/// <summary>The kinds of data this operation declares it may transmit.</summary>
	InferenceDataKind DataKinds { get; }

	/// <summary>The maximum serialized UTF-8 input size; larger inputs fail without transmission.</summary>
	int MaxInputBytes { get; }

	/// <summary>The maximum UTF-8 bytes accepted from the structured result.</summary>
	int MaxOutputBytes { get; }

	/// <summary>The single model attempt's time budget.</summary>
	TimeSpan TimeBudget { get; }
}

/// <summary>
/// A registered, typed query recipe. The provider receives a schema derived from <typeparamref name="TOutput"/>;
/// Weavie then decodes that JSON locally and runs <see cref="Validate"/> before accepting it.
/// </summary>
/// <typeparam name="TInput">The serialized input shape.</typeparam>
/// <typeparam name="TOutput">The strict structured-output shape.</typeparam>
public sealed record InferenceOperation<TInput, TOutput> : IInferenceOperation {
	/// <inheritdoc/>
	public required string Id { get; init; }

	/// <inheritdoc/>
	public required string Instructions { get; init; }

	/// <inheritdoc/>
	public required IReadOnlyList<InferenceModelCategory> AllowedCategories { get; init; }

	/// <inheritdoc/>
	public required InferenceDataKind DataKinds { get; init; }

	/// <inheritdoc/>
	public required int MaxInputBytes { get; init; }

	/// <inheritdoc/>
	public required int MaxOutputBytes { get; init; }

	/// <inheritdoc/>
	public required TimeSpan TimeBudget { get; init; }

	/// <summary>The source-generated contract used to serialize input.</summary>
	public required JsonTypeInfo<TInput> InputType { get; init; }

	/// <summary>The source-generated contract used to generate a strict schema and decode output.</summary>
	public required JsonTypeInfo<TOutput> OutputType { get; init; }

	/// <summary>Returns null for a valid decoded value, or a stable rejection reason.</summary>
	public required Func<TOutput, string?> Validate { get; init; }
}

/// <summary>The closed catalog of code-reviewed inference operations.</summary>
public sealed class InferenceOperationRegistry {
	private readonly Dictionary<string, IInferenceOperation> _operations = new(StringComparer.Ordinal);

	/// <summary>Registers one operation and rejects invalid or duplicate declarations.</summary>
	public void Register(IInferenceOperation operation) {
		ArgumentNullException.ThrowIfNull(operation);
		ArgumentException.ThrowIfNullOrWhiteSpace(operation.Id);
		ArgumentException.ThrowIfNullOrWhiteSpace(operation.Instructions);
		if (operation.AllowedCategories.Count == 0) {
			throw new ArgumentException($"Inference operation '{operation.Id}' allows no model categories.", nameof(operation));
		}
		if (operation.MaxInputBytes <= 0 || operation.MaxOutputBytes <= 0 || operation.TimeBudget <= TimeSpan.Zero) {
			throw new ArgumentException($"Inference operation '{operation.Id}' must declare positive bounds.", nameof(operation));
		}
		if (!_operations.TryAdd(operation.Id, operation)) {
			throw new InvalidOperationException($"Inference operation '{operation.Id}' is already registered.");
		}
	}

	/// <summary>Requires that <paramref name="operation"/> is the exact registered declaration.</summary>
	public void RequireRegistered(IInferenceOperation operation) {
		ArgumentNullException.ThrowIfNull(operation);
		if (!_operations.TryGetValue(operation.Id, out var registered) || !ReferenceEquals(registered, operation)) {
			throw new InvalidOperationException($"Inference operation '{operation.Id}' is not registered.");
		}
	}
}
