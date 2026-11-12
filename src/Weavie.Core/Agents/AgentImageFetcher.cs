namespace Weavie.Core.Agents;

/// <summary>A remote image fetched for an agent's reply.</summary>
public sealed record AgentImage(string ContentType, byte[] Bytes);

/// <summary>Why a remote image was not loaded; the message is shown in the image's place.</summary>
public sealed class AgentImageRefusedException(string message) : Exception(message);

/// <summary>
/// Fetches the https images an agent's reply references, from the machine the agent runs on, so the pane shows them
/// same-origin. Only passive raster images load, and none larger than <see cref="MaxBytes"/>.
/// </summary>
public sealed class AgentImageFetcher(HttpMessageHandler handler) : IDisposable {
	/// <summary>The largest image loaded.</summary>
	public const int MaxBytes = 20 * 1024 * 1024;

	// A fetch ends with its request or its session, never on a timer.
	private readonly HttpClient _client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };

	/// <summary>Fetches <paramref name="address"/>, refusing it when <paramref name="enabled"/> is false or it is not an image.</summary>
	public async Task<AgentImage> FetchAsync(string address, bool enabled, CancellationToken ct) {
		if (!enabled) throw new AgentImageRefusedException("Remote images are off (setting agent.remoteImages).");
		if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
			|| !uri.IsDefaultPort || uri.UserInfo.Length > 0) {
			throw new AgentImageRefusedException("Only https images on the default port, without a user name, load.");
		}
		HttpResponseMessage response;
		try {
			response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
		} catch (HttpRequestException error) {
			throw new AgentImageRefusedException($"The image could not be fetched: {error.Message}");
		}
		using (response) {
			if (!response.IsSuccessStatusCode) {
				throw new AgentImageRefusedException($"The image server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
			}
			string type = response.Content.Headers.ContentType?.MediaType ?? "an unknown type";
			if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
				|| type.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase)) {
				throw new AgentImageRefusedException($"The address served {type}, which is not a raster image.");
			}
			if (response.Content.Headers.ContentLength > MaxBytes) throw TooLarge();
			using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
			using var buffer = new MemoryStream();
			byte[] chunk = new byte[81920];
			for (int read; (read = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0;) {
				if (buffer.Length + read > MaxBytes) throw TooLarge();
				buffer.Write(chunk, 0, read);
			}
			return new AgentImage(type, buffer.ToArray());
		}
	}

	/// <inheritdoc/>
	public void Dispose() => _client.Dispose();

	private static AgentImageRefusedException TooLarge() =>
		new($"The image is larger than the {MaxBytes / (1024 * 1024)} MiB limit.");
}
