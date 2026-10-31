using System.Diagnostics;

namespace Weavie.Core.Diagnostics;

/// <summary>Logs an operation that took long enough to explain a visible wait, so slow machines leave a trail.</summary>
public static class SlowOperationLog {
	private static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(250);

	/// <summary>Logs <paramref name="what"/> if it has run for the threshold or longer since <paramref name="started"/>.</summary>
	public static void Report(string what, long started) {
		var elapsed = Stopwatch.GetElapsedTime(started);
		if (elapsed >= Threshold) {
			Console.WriteLine($"[slow] {what} took {elapsed.TotalMilliseconds:F0}ms");
		}
	}
}

/// <summary>Logs each slow HTTP round trip through <see cref="SlowOperationLog"/>.</summary>
public sealed class SlowRequestHandler() : DelegatingHandler(new HttpClientHandler()) {
	/// <inheritdoc/>
	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
		long started = Stopwatch.GetTimestamp();
		try {
			return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
		} finally {
			SlowOperationLog.Report($"{request.Method} {request.RequestUri?.AbsolutePath}", started);
		}
	}
}
