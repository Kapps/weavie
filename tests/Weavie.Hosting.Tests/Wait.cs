namespace Weavie.Hosting.Tests;

/// <summary>Polling wait for bridge assertions: retries a selector until it yields a value, else times out.</summary>
internal static class Wait {
	public static Task UntilAsync(Func<bool> condition) =>
		UntilAsync(condition, TimeSpan.FromSeconds(5));

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

	public static async Task<T> ForAsync<T>(Func<T?> selector) where T : struct {
		for (int i = 0; i < 200; i++) {
			if (selector() is { } value) {
				return value;
			}

			await Task.Delay(25);
		}

		throw new TimeoutException("Condition was not met within the timeout.");
	}

	public static async Task<T> ForReferenceAsync<T>(Func<T?> selector) where T : class {
		for (int i = 0; i < 200; i++) {
			if (selector() is { } value) {
				return value;
			}

			await Task.Delay(25);
		}

		throw new TimeoutException("Condition was not met within the timeout.");
	}
}
