namespace Weavie.WorktreeServe;

internal sealed class HeadlessReadiness {
	private const string TokenPrefix = "[weavie-headless] token ";
	private const string OpenPrefix = "[weavie-headless] open  ";
	private readonly Lock _gate = new();
	private readonly TaskCompletionSource<HeadlessEndpoint> _ready =
		new(TaskCreationOptions.RunContinuationsAsynchronously);
	private string? _token;
	private Uri? _pageUrl;

	public Task<HeadlessEndpoint> Ready => _ready.Task;

	public static bool IsTokenLine(string line) => line.StartsWith(TokenPrefix, StringComparison.Ordinal);

	public void Accept(string line) {
		lock (_gate) {
			if (line.StartsWith(TokenPrefix, StringComparison.Ordinal)) {
				_token = line[TokenPrefix.Length..].Trim();
			} else if (line.StartsWith(OpenPrefix, StringComparison.Ordinal)) {
				string candidate = line[OpenPrefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
				if (Uri.TryCreate(candidate, UriKind.Absolute, out var pageUrl)
					&& pageUrl.Scheme == Uri.UriSchemeHttp
					&& pageUrl.Host == "127.0.0.1"
					&& pageUrl.Port > 0) {
					_pageUrl = pageUrl;
				}
			}

			if (_token is { Length: > 0 } token && _pageUrl is { } url) {
				_ready.TrySetResult(new HeadlessEndpoint(url, token));
			}
		}
	}
}

internal sealed record HeadlessEndpoint(Uri PageUrl, string Token) {
	public string Target => $"{PageUrl.Scheme}://{PageUrl.Host}:{PageUrl.Port}";

	public string BrowserUrl(string magicDns, int httpsPort) {
		var builder = new UriBuilder(Uri.UriSchemeHttps, magicDns, httpsPort, "/index.html") {
			Fragment = $"token={Uri.EscapeDataString(Token)}",
		};
		return builder.Uri.AbsoluteUri;
	}
}
