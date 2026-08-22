using System.Text.Json.Serialization.Metadata;
using Weavie.Core.Inference;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The inference rail for a session under test that never revises. It throws rather than returning a failure, so a
/// test that starts depending on inference fails loudly instead of silently taking a disabled path.
/// </summary>
internal sealed class UnusedInferenceService : IInferenceService {
	public static UnusedInferenceService Instance { get; } = new();

	public Task<InferenceResult<TResponse>> QueryAsync<TResponse>(
		InferenceOwner owner,
		InferenceModelCategory category,
		InferenceInput input,
		JsonTypeInfo<TResponse> responseType,
		InferenceQueryOptions options,
		CancellationToken ct) =>
		throw new NotSupportedException("This test's session does not exercise inference.");
}
