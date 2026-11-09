using System.Diagnostics;
using Weavie.Core.Configuration;

namespace Weavie.Core.Inference;

/// <summary>The policy gates and one-attempt timing shared by every ad-hoc inference surface.</summary>
internal static class InferencePolicy {
	/// <summary>The failure the live settings impose on a query from <paramref name="origin"/>, or null when allowed.</summary>
	public static InferenceFailureKind? Gate(SettingsStore settings, InferenceInvocationOrigin origin, out string detail) {
		if (!settings.RequireBool(InferenceSettings.Enabled)) {
			detail = "Ad-hoc inference is disabled.";
			return InferenceFailureKind.Disabled;
		}
		if (origin == InferenceInvocationOrigin.Automatic && !settings.RequireBool(InferenceSettings.AllowAutomatic)) {
			detail = "Automatic inference is disabled.";
			return InferenceFailureKind.PolicyDenied;
		}
		detail = "";
		return null;
	}

	/// <summary>
	/// Runs one attempt bounded by <paramref name="budget"/>. Exceeding it reports TimedOut; caller cancellation
	/// propagates, and any other exception escapes to the caller.
	/// </summary>
	public static async Task<(T? Value, TimeSpan Elapsed, bool TimedOut)> AttemptAsync<T>(
		TimeSpan budget,
		Func<CancellationToken, Task<T>> attempt,
		CancellationToken ct) {
		var stopwatch = Stopwatch.StartNew();
		using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
		bounded.CancelAfter(budget);
		T value;
		try {
			value = await attempt(bounded.Token).ConfigureAwait(false);
		} catch (Exception ex) when (!ct.IsCancellationRequested
			&& (ex is OperationCanceledException || bounded.IsCancellationRequested)) {
			// An attempt torn down at its budget may fail with whatever its transport throws, not only cancellation.
			return (default, stopwatch.Elapsed, true);
		}
		ct.ThrowIfCancellationRequested();
		return (value, stopwatch.Elapsed, false);
	}
}
