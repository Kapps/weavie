using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Weavie.Core.Configuration;

namespace Weavie.Core.Inference.Decisions;

/// <summary>The decision surface: one choice question over typed evidence, answered by the live decision provider.</summary>
public interface IDecisionService {
	/// <summary>Every registered decision provider.</summary>
	IReadOnlyList<IDecisionProvider> Providers { get; }

	/// <summary>
	/// Runs one attempt through the provider <c>inference.decisionProvider</c> names. Non-cancellation failures,
	/// refusals included, are returned.
	/// </summary>
	Task<DecisionResult> DecideAsync<TEvidence>(
		InferenceOwner owner,
		TEvidence evidence,
		JsonTypeInfo<TEvidence> evidenceType,
		DecisionQuestion question,
		DecisionQueryOptions options,
		CancellationToken ct);
}

/// <summary>
/// Enforces the inference policy and time budget around the selected provider and validates its distribution: every
/// label present, none unknown, each finite within [0, 1], and one unique maximum. It never repairs a distribution.
/// </summary>
public sealed class DecisionService : IDecisionService {
	private readonly SettingsStore _settings;

	/// <summary>Creates the service over live settings and the registered providers.</summary>
	public DecisionService(SettingsStore settings, IReadOnlyList<IDecisionProvider> providers) {
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(providers);
		_settings = settings;
		Providers = providers;
	}

	/// <inheritdoc/>
	public IReadOnlyList<IDecisionProvider> Providers { get; }

	/// <inheritdoc/>
	public async Task<DecisionResult> DecideAsync<TEvidence>(
		InferenceOwner owner,
		TEvidence evidence,
		JsonTypeInfo<TEvidence> evidenceType,
		DecisionQuestion question,
		DecisionQueryOptions options,
		CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(owner);
		ArgumentException.ThrowIfNullOrWhiteSpace(owner.Workspace);
		ArgumentNullException.ThrowIfNull(evidenceType);
		ArgumentNullException.ThrowIfNull(question);
		ArgumentException.ThrowIfNullOrWhiteSpace(question.Instructions);
		ArgumentNullException.ThrowIfNull(question.Choices);
		if (question.Choices.Count < 2
			|| question.Choices.Any(choice => string.IsNullOrWhiteSpace(choice.Label))
			|| question.Choices.DistinctBy(choice => choice.Label).Count() != question.Choices.Count) {
			throw new ArgumentException("A decision needs at least two choices with unique labels.", nameof(question));
		}
		ArgumentNullException.ThrowIfNull(options);
		if (options.TimeBudget <= TimeSpan.Zero) {
			throw new ArgumentException("A decision's time budget must be positive.", nameof(options));
		}

		ct.ThrowIfCancellationRequested();
		if (InferencePolicy.Gate(_settings, options.Origin, out string denied) is { } gate) {
			return new DecisionFailure { Kind = gate, Detail = denied };
		}
		string id = _settings.RequireString(InferenceSettings.DecisionProvider);
		if (Providers.FirstOrDefault(provider => provider.Id == id) is not { } selected) {
			return new DecisionFailure {
				Kind = InferenceFailureKind.NotConfigured,
				Detail = $"Decision provider '{id}' is not available.",
			};
		}

		var request = new DecisionProviderRequest {
			Owner = owner,
			Evidence = JsonSerializer.SerializeToElement(evidence, evidenceType),
			Question = question,
			Origin = options.Origin,
			TimeBudget = options.TimeBudget,
		};
		var (result, elapsed, timedOut) = await InferencePolicy.AttemptAsync(
			options.TimeBudget,
			token => selected.DecideAsync(request, token),
			ct).ConfigureAwait(false);
		if (timedOut) {
			return new DecisionFailure { Kind = InferenceFailureKind.TimedOut, Detail = "The decision exceeded its time budget." };
		}

		var receipt = new InferenceReceipt {
			ProviderId = id,
			ModelId = result!.ModelId,
			Duration = elapsed,
			RequestId = result.RequestId,
			Usage = result.Usage,
		};
		return result switch {
			DecisionProviderFailure failure => new DecisionFailure { Kind = failure.Kind, Detail = failure.Detail, Receipt = receipt },
			DecisionDistribution distribution => Validate(question, distribution.Probabilities, receipt),
			_ => throw new InvalidOperationException($"Decision provider '{id}' returned an unknown result type."),
		};
	}

	private static DecisionResult Validate(
		DecisionQuestion question,
		IReadOnlyDictionary<string, double> probabilities,
		InferenceReceipt receipt) {
		string? problem = null;
		if (probabilities.Keys.FirstOrDefault(label => question.Choices.All(choice => choice.Label != label)) is { } unknown) {
			problem = $"The provider returned unknown label '{unknown}'.";
		} else if (question.Choices.FirstOrDefault(choice => !probabilities.ContainsKey(choice.Label)) is { } missing) {
			problem = $"The provider returned no probability for '{missing.Label}'.";
		} else if (probabilities.Values.Any(p => !double.IsFinite(p) || p < 0 || p > 1)) {
			problem = "The provider returned a probability outside [0, 1].";
		}
		if (problem is not null) {
			return new DecisionFailure { Kind = InferenceFailureKind.InvalidResponse, Detail = problem, Receipt = receipt };
		}

		double top = probabilities.Values.Max();
		var leaders = probabilities.Where(pair => pair.Value == top).ToList();
		if (leaders.Count > 1) {
			return new DecisionFailure {
				Kind = InferenceFailureKind.InvalidResponse,
				Detail = $"The provider tied {string.Join(" and ", leaders.Select(pair => $"'{pair.Key}'"))}.",
				Receipt = receipt,
			};
		}
		return new DecisionAnswer {
			Choice = leaders[0].Key,
			Probability = top,
			Probabilities = probabilities,
			Receipt = receipt,
		};
	}
}
