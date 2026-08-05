using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Weavie.Core.Agents;
using Weavie.Core.Configuration;

namespace Weavie.Core.Inference;

/// <summary>The typed query surface consumed by feature-owned inference recipes.</summary>
public interface IInferenceService {
	/// <summary>
	/// Runs one schema-constrained attempt. Non-cancellation failures are returned so the feature can execute the
	/// exact behavior it uses when inference is disabled.
	/// </summary>
	Task<InferenceResult<TResponse>> QueryAsync<TResponse>(
		string agentProviderId,
		InferenceModelCategory category,
		string prompt,
		JsonTypeInfo<TResponse> responseType,
		InferenceQueryOptions options,
		CancellationToken ct);
}

/// <summary>
/// Enforces policy, bounds, category support, one-attempt timing, and strict typed decoding around the selected
/// installed agent provider's stateless facet.
/// </summary>
public sealed class InferenceService : IInferenceService {
	private static readonly JsonSchemaExporterOptions SchemaOptions = new() {
		TreatNullObliviousAsNonNullable = true,
	};
	private readonly SettingsStore _settings;
	private readonly AgentProviderRegistry _agentProviders;

	/// <summary>Creates the service over live settings and the installed agent-provider catalog.</summary>
	public InferenceService(
		SettingsStore settings,
		AgentProviderRegistry agentProviders) {
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(agentProviders);
		_settings = settings;
		_agentProviders = agentProviders;
	}

	/// <inheritdoc/>
	public async Task<InferenceResult<TResponse>> QueryAsync<TResponse>(
		string agentProviderId,
		InferenceModelCategory category,
		string prompt,
		JsonTypeInfo<TResponse> responseType,
		InferenceQueryOptions options,
		CancellationToken ct) {
		ArgumentException.ThrowIfNullOrWhiteSpace(agentProviderId);
		ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
		ArgumentNullException.ThrowIfNull(responseType);
		ArgumentNullException.ThrowIfNull(options);
		ValidateQuery(responseType, options);

		ct.ThrowIfCancellationRequested();
		if (!_settings.RequireBool(InferenceSettings.Enabled)) {
			return Failure<TResponse>(InferenceFailureKind.Disabled, "Ad-hoc inference is disabled.");
		}
		if (options.Origin == InferenceInvocationOrigin.Automatic
			&& !_settings.RequireBool(InferenceSettings.AllowAutomatic)) {
			return Failure<TResponse>(InferenceFailureKind.PolicyDenied, "Automatic inference is disabled.");
		}
		if (Encoding.UTF8.GetByteCount(prompt) > options.MaxPromptBytes) {
			return Failure<TResponse>(InferenceFailureKind.InputRejected, "The inference prompt exceeds its declared size limit.");
		}

		IAgentProvider agentProvider;
		try {
			agentProvider = _agentProviders.RequireAvailable(agentProviderId);
		} catch (InvalidOperationException ex) {
			return Failure<TResponse>(
				InferenceFailureKind.NotConfigured,
				ex.Message);
		}
		if (agentProvider is not IAgentInferenceProvider provider) {
			return Failure<TResponse>(
				InferenceFailureKind.NotConfigured,
				$"Agent provider '{agentProviderId}' does not support ad-hoc inference.");
		}
		if (!provider.InferenceInfo.Categories.Contains(category)) {
			return Failure<TResponse>(
				InferenceFailureKind.CategoryUnavailable,
				$"Agent provider '{agentProviderId}' does not support inference category '{category}'.");
		}

		string schema = JsonSchemaExporter.GetJsonSchemaAsNode(responseType, SchemaOptions).ToJsonString();
		var request = new InferenceProviderRequest {
			Category = category,
			Prompt = prompt,
			OutputSchemaJson = schema,
			MaxOutputBytes = options.MaxOutputBytes,
		};

		var stopwatch = Stopwatch.StartNew();
		using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
		attempt.CancelAfter(options.TimeBudget);
		InferenceProviderResult providerResult;
		try {
			providerResult = await provider.QueryInferenceAsync(request, attempt.Token).ConfigureAwait(false);
		} catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
			return Failure<TResponse>(
				InferenceFailureKind.TimedOut,
				"The inference query exceeded its time budget.");
		} catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException) {
			ct.ThrowIfCancellationRequested();
			if (attempt.IsCancellationRequested) {
				return Failure<TResponse>(
					InferenceFailureKind.TimedOut,
					"The inference query exceeded its time budget.");
			}
			return Failure<TResponse>(
				InferenceFailureKind.ProviderUnavailable,
				$"Agent provider '{agentProviderId}' could not complete the inference process.");
		}
		ct.ThrowIfCancellationRequested();
		stopwatch.Stop();

		var receipt = new InferenceReceipt {
			ProviderId = agentProviderId,
			Category = category,
			ModelId = providerResult.ModelId,
			Duration = stopwatch.Elapsed,
			RequestId = providerResult.RequestId,
			Usage = providerResult.Usage,
		};
		if (providerResult is InferenceProviderFailure providerFailure) {
			return new InferenceFailure<TResponse> {
				Kind = providerFailure.Kind,
				Detail = providerFailure.Detail,
				Receipt = receipt,
			};
		}
		if (providerResult is not InferenceProviderSuccess success) {
			throw new InvalidOperationException(
				$"Agent provider '{agentProviderId}' returned an unknown inference result type.");
		}
		if (Encoding.UTF8.GetByteCount(success.OutputJson) > options.MaxOutputBytes) {
			return Invalid<TResponse>(receipt, "The provider's structured result exceeds the query's output limit.");
		}

		TResponse? value;
		try {
			value = JsonSerializer.Deserialize(success.OutputJson, responseType);
		} catch (JsonException) {
			return Invalid<TResponse>(receipt, "The provider returned JSON that does not match the declared response type.");
		}
		if (value is null) {
			return Invalid<TResponse>(receipt, "The provider returned a null structured result.");
		}

		return new InferenceSuccess<TResponse> { Value = value, Receipt = receipt };
	}

	private static void ValidateQuery<TResponse>(
		JsonTypeInfo<TResponse> responseType,
		InferenceQueryOptions options) {
		if (options.MaxPromptBytes <= 0 || options.MaxOutputBytes <= 0 || options.TimeBudget <= TimeSpan.Zero) {
			throw new ArgumentException("Inference query bounds must be positive.", nameof(options));
		}
		if (responseType.Options.UnmappedMemberHandling != JsonUnmappedMemberHandling.Disallow
			|| !responseType.Options.RespectRequiredConstructorParameters) {
			throw new InvalidOperationException(
				"Inference response metadata must disallow unmapped members and respect required constructor parameters.");
		}
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
}
