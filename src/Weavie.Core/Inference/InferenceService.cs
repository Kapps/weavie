using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Schema;
using Weavie.Core.Agents;
using Weavie.Core.Configuration;

namespace Weavie.Core.Inference;

/// <summary>The typed query surface consumed by feature-owned inference recipes.</summary>
public interface IInferenceService {
	/// <summary>
	/// Runs one schema-constrained attempt. Non-cancellation failures are returned so the feature can execute the
	/// exact behavior it uses when inference is disabled.
	/// </summary>
	Task<InferenceResult<TOutput>> RunAsync<TInput, TOutput>(
		InferenceOperation<TInput, TOutput> operation,
		string agentProviderId,
		InferenceModelCategory category,
		TInput input,
		InferenceInvocationOrigin origin,
		CancellationToken ct);
}

/// <summary>
/// Enforces operation registration, policy, bounds, category support, one-attempt timing, strict decoding, and
/// domain validation around the selected installed agent provider's stateless facet.
/// </summary>
public sealed class InferenceService : IInferenceService {
	private static readonly JsonSchemaExporterOptions SchemaOptions = new() {
		TreatNullObliviousAsNonNullable = true,
	};
	private readonly SettingsStore _settings;
	private readonly InferenceOperationRegistry _operations;
	private readonly AgentProviderRegistry _agentProviders;

	/// <summary>Creates the service over live settings and closed operation/provider catalogs.</summary>
	public InferenceService(
		SettingsStore settings,
		InferenceOperationRegistry operations,
		AgentProviderRegistry agentProviders) {
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(operations);
		ArgumentNullException.ThrowIfNull(agentProviders);
		_settings = settings;
		_operations = operations;
		_agentProviders = agentProviders;
	}

	/// <inheritdoc/>
	public async Task<InferenceResult<TOutput>> RunAsync<TInput, TOutput>(
		InferenceOperation<TInput, TOutput> operation,
		string agentProviderId,
		InferenceModelCategory category,
		TInput input,
		InferenceInvocationOrigin origin,
		CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(operation);
		ArgumentException.ThrowIfNullOrWhiteSpace(agentProviderId);
		_operations.RequireRegistered(operation);
		if (!operation.AllowedCategories.Contains(category)) {
			throw new InvalidOperationException(
				$"Inference operation '{operation.Id}' does not allow category '{category}'.");
		}

		ct.ThrowIfCancellationRequested();
		if (!_settings.RequireBool(InferenceSettings.Enabled)) {
			return Failure<TOutput>(InferenceFailureKind.Disabled, "Ad-hoc inference is disabled.");
		}
		if (origin == InferenceInvocationOrigin.Automatic
			&& !_settings.RequireBool(InferenceSettings.AllowAutomatic)) {
			return Failure<TOutput>(InferenceFailureKind.PolicyDenied, "Automatic inference is disabled.");
		}

		IAgentProvider agentProvider;
		try {
			agentProvider = _agentProviders.RequireAvailable(agentProviderId);
		} catch (InvalidOperationException ex) {
			return Failure<TOutput>(
				InferenceFailureKind.NotConfigured,
				ex.Message);
		}
		if (agentProvider is not IAgentInferenceProvider provider) {
			return Failure<TOutput>(
				InferenceFailureKind.NotConfigured,
				$"Agent provider '{agentProviderId}' does not support ad-hoc inference.");
		}
		if (!provider.InferenceInfo.Categories.Contains(category)) {
			return Failure<TOutput>(
				InferenceFailureKind.CategoryUnavailable,
				$"Agent provider '{agentProviderId}' does not support inference category '{category}'.");
		}

		string inputJson = JsonSerializer.Serialize(input, operation.InputType);
		if (Encoding.UTF8.GetByteCount(inputJson) > operation.MaxInputBytes) {
			return Failure<TOutput>(
				InferenceFailureKind.InputRejected,
				$"Inference input for '{operation.Id}' exceeds its declared size limit.");
		}

		string schema = JsonSchemaExporter.GetJsonSchemaAsNode(operation.OutputType, SchemaOptions).ToJsonString();
		var request = new InferenceProviderRequest {
			OperationId = operation.Id,
			Category = category,
			Instructions = operation.Instructions,
			InputJson = inputJson,
			OutputSchemaJson = schema,
			OutputSchemaName = SchemaName(operation.Id),
			MaxOutputBytes = operation.MaxOutputBytes,
		};

		var stopwatch = Stopwatch.StartNew();
		using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
		attempt.CancelAfter(operation.TimeBudget);
		InferenceProviderResult providerResult;
		try {
			providerResult = await provider.QueryInferenceAsync(request, attempt.Token).ConfigureAwait(false);
		} catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
			return Failure<TOutput>(
				InferenceFailureKind.TimedOut,
				$"Inference operation '{operation.Id}' exceeded its time budget.");
		} catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException) {
			ct.ThrowIfCancellationRequested();
			if (attempt.IsCancellationRequested) {
				return Failure<TOutput>(
					InferenceFailureKind.TimedOut,
					$"Inference operation '{operation.Id}' exceeded its time budget.");
			}
			return Failure<TOutput>(
				InferenceFailureKind.ProviderUnavailable,
				$"Agent provider '{agentProviderId}' could not complete the inference process.");
		}
		ct.ThrowIfCancellationRequested();
		stopwatch.Stop();

		var receipt = new InferenceReceipt {
			OperationId = operation.Id,
			ProviderId = agentProviderId,
			Category = category,
			ModelId = providerResult.ModelId,
			Duration = stopwatch.Elapsed,
			RequestId = providerResult.RequestId,
			Usage = providerResult.Usage,
		};
		if (providerResult is InferenceProviderFailure providerFailure) {
			return new InferenceFailure<TOutput> {
				Kind = providerFailure.Kind,
				Detail = providerFailure.Detail,
				Receipt = receipt,
			};
		}
		if (providerResult is not InferenceProviderSuccess success) {
			throw new InvalidOperationException(
				$"Agent provider '{agentProviderId}' returned an unknown inference result type.");
		}
		if (Encoding.UTF8.GetByteCount(success.OutputJson) > operation.MaxOutputBytes) {
			return Invalid<TOutput>(receipt, "The provider's structured result exceeds the operation's output limit.");
		}

		TOutput? value;
		try {
			value = JsonSerializer.Deserialize(success.OutputJson, operation.OutputType);
		} catch (JsonException) {
			return Invalid<TOutput>(receipt, "The provider returned JSON that does not match the declared output type.");
		}
		if (value is null) {
			return Invalid<TOutput>(receipt, "The provider returned a null structured result.");
		}

		string? validationError = operation.Validate(value);
		return validationError is null
			? new InferenceSuccess<TOutput> { Value = value, Receipt = receipt }
			: Invalid<TOutput>(receipt, validationError);
	}

	private static InferenceFailure<T> Failure<T>(InferenceFailureKind kind, string detail) => new() {
		Kind = kind,
		Detail = detail,
	};

	private static InferenceFailure<T> Invalid<T>(InferenceReceipt receipt, string detail) => new() {
		Kind = InferenceFailureKind.InvalidResponse,
		Detail = detail,
		Receipt = receipt,
	};

	private static string SchemaName(string operationId) =>
		string.Concat(operationId.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
}
