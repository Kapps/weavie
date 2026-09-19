using System.Net;

namespace Weavie.Core.Theming;

public sealed partial class OpenVsxThemeInstaller {
	private async Task<HttpResponseMessage> GetRegistryAsync(string url, CancellationToken ct) {
		var remainingDelay = TimeSpan.FromSeconds(3);
		for (int attempt = 0; ; attempt++) {
			var delay = TimeSpan.FromSeconds(attempt + 1);
			try {
				var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
				if (response.IsSuccessStatusCode) return response;
				using (response) {
					var retryAfter = response.Headers.RetryAfter;
					var requestedDelay = retryAfter?.Delta
						?? (retryAfter?.Date - DateTimeOffset.UtcNow)
						?? TimeSpan.Zero;
					if (requestedDelay > delay) delay = requestedDelay;
					if (attempt >= 2 || delay > remainingDelay || !IsTransientStatus(response.StatusCode)) {
						response.EnsureSuccessStatusCode();
					}
				}
			} catch (HttpRequestException ex) when (attempt < 2 && !ct.IsCancellationRequested
				&& ex.StatusCode is null && ex.HttpRequestError is HttpRequestError.Unknown
					or HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded) {
				if (delay > remainingDelay) throw;
			} catch (OperationCanceledException) when (attempt < 2 && !ct.IsCancellationRequested) {
				if (delay > remainingDelay) throw;
			}
			await Task.Delay(delay, ct).ConfigureAwait(false);
			remainingDelay -= delay;
		}
	}

	private static bool IsTransientStatus(HttpStatusCode status) => status is
		HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
		or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
}
