using Weavie.Core.Configuration;

namespace Weavie.Core.Inference.Decisions;

/// <summary>The decision surface: one choice question over evidence, answered by the live decision provider.</summary>
public interface IDecisionService {
	/// <summary>Every registered decision provider.</summary>
	IReadOnlyList<IDecisionProvider> Providers { get; }

	/// <summary>Runs one attempt through the provider <c>inference.decisionProvider</c> names; non-cancellation failures,
	/// refusals included, are returned.</summary>
	Task<InferenceResult<DecisionAnswer>> DecideAsync(DecisionRequest request, CancellationToken ct);
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
	public async Task<InferenceResult<DecisionAnswer>> DecideAsync(DecisionRequest request, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(request);
		ArgumentException.ThrowIfNullOrWhiteSpace(request.Owner.Workspace);
		var question = request.Question;
		ArgumentException.ThrowIfNullOrWhiteSpace(question.Instructions);
		if (question.Choices.Count < 2
			|| question.Choices.Any(choice => string.IsNullOrWhiteSpace(choice.Label))
			|| question.Choices.DistinctBy(choice => choice.Label).Count() != question.Choices.Count) {
			throw new ArgumentException("A decision needs at least two choices with unique labels.", nameof(request));
		}
		if (request.TimeBudget <= TimeSpan.Zero) {
			throw new ArgumentException("A decision's time budget must be positive.", nameof(request));
		}

		ct.ThrowIfCancellationRequested();
		if (InferencePolicy.Gate(_settings, request.Origin, out string denied) is { } gate) {
			return Failure(gate, denied, null);
		}
		string id = _settings.RequireString(InferenceSettings.DecisionProvider);
		if (Providers.FirstOrDefault(provider => provider.Id == id) is not { } selected) {
			return Failure(InferenceFailureKind.NotConfigured, $"Decision provider '{id}' is not available.", null);
		}

		var (result, elapsed, timedOut) = await InferencePolicy.AttemptAsync(
			request.TimeBudget, token => selected.DecideAsync(request, token), ct).ConfigureAwait(false);
		if (timedOut) {
			return Failure(InferenceFailureKind.TimedOut, "The decision exceeded its time budget.", null);
		}

		var receipt = result!.Receipt(id, elapsed);
		return result switch {
			InferenceProviderFailure failure => Failure(failure.Kind, failure.Detail, receipt),
			DecisionDistribution distribution => Validate(question, distribution.Probabilities, receipt),
			_ => throw new InvalidOperationException($"Decision provider '{id}' returned an unknown result type."),
		};
	}

	private static InferenceResult<DecisionAnswer> Validate(
		DecisionQuestion question, IReadOnlyDictionary<string, double> probabilities, InferenceReceipt receipt) {
		string? problem = null;
		if (probabilities.Keys.FirstOrDefault(label => question.Choices.All(choice => choice.Label != label)) is { } unknown) {
			problem = $"The provider returned unknown label '{unknown}'.";
		} else if (question.Choices.FirstOrDefault(choice => !probabilities.ContainsKey(choice.Label)) is { } missing) {
			problem = $"The provider returned no probability for '{missing.Label}'.";
		} else if (probabilities.Values.Any(p => !double.IsFinite(p) || p < 0 || p > 1)) {
			problem = "The provider returned a probability outside [0, 1].";
		} else if (probabilities.Values.Max() is var max && probabilities.Where(pair => pair.Value == max).ToList() is { Count: > 1 } tied) {
			problem = $"The provider tied {string.Join(" and ", tied.Select(pair => $"'{pair.Key}'"))}.";
		}
		if (problem is not null) {
			return Failure(InferenceFailureKind.InvalidResponse, problem, receipt);
		}

		var top = probabilities.MaxBy(pair => pair.Value);
		return new InferenceSuccess<DecisionAnswer> { Value = new(top.Key, top.Value, probabilities), Receipt = receipt };
	}

	private static InferenceFailure<DecisionAnswer> Failure(InferenceFailureKind kind, string detail, InferenceReceipt? receipt) =>
		new() { Kind = kind, Detail = detail, Receipt = receipt };
}
