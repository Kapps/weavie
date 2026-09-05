namespace Weavie.TestSupport;

/// <summary>Polling wait for asynchronous assertions: retries a condition or selector until it yields, else times out.</summary>
public static class Wait {
	/// <summary>Waits up to five seconds for <paramref name="condition"/> to hold.</summary>
	public static Task UntilAsync(Func<bool> condition) =>
		UntilAsync(condition, TimeSpan.FromSeconds(5));

	/// <summary>Waits up to <paramref name="timeout"/> for <paramref name="condition"/> to hold.</summary>
	public static async Task UntilAsync(Func<bool> condition, TimeSpan timeout) {
		ArgumentNullException.ThrowIfNull(condition);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
		using var stopping = new CancellationTokenSource(timeout);
		while (!stopping.IsCancellationRequested) {
			if (condition()) {
				return;
			}

			try {
				await Task.Delay(25, stopping.Token);
			} catch (OperationCanceledException) when (stopping.IsCancellationRequested) {
			}
		}

		throw new TimeoutException("Condition was not met within the timeout.");
	}

	/// <summary>Waits for <paramref name="selector"/> to produce a value.</summary>
	public static async Task<T> ForAsync<T>(Func<T?> selector) where T : struct {
		ArgumentNullException.ThrowIfNull(selector);
		T? found = null;
		await UntilAsync(() => (found = selector()) is not null);
		return found!.Value;
	}

	/// <summary>Waits for <paramref name="selector"/> to produce a reference.</summary>
	public static async Task<T> ForReferenceAsync<T>(Func<T?> selector) where T : class {
		ArgumentNullException.ThrowIfNull(selector);
		T? found = null;
		await UntilAsync(() => (found = selector()) is not null);
		return found!;
	}
}
