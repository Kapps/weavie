using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Weavie.Core.Credentials;

namespace Weavie.Core.Inference.Decisions;

/// <summary>
/// A decision vendor called over HTTP with the user's saved key: the bearer header (the only place a vendor key is
/// revealed), one POST with no retry, and each failure mapped to a kind whose detail never carries a raw body or key.
/// </summary>
public abstract class VendorDecisionProvider : IDecisionProvider {
	/// <summary>The question name every vendor request and reply uses.</summary>
	protected const string Question = "decision";

	private readonly HttpClient _http;
	private readonly ICredentialStore _store;

	/// <summary>Declares the vendor's key, checked by listing <paramref name="modelsUrl"/>.</summary>
	protected VendorDecisionProvider(
		HttpClient http, ICredentialStore store, string id, string displayName, string keyPageUrl, string modelsUrl) {
		ArgumentNullException.ThrowIfNull(http);
		ArgumentNullException.ThrowIfNull(store);
		_http = http;
		_store = store;
		Credential = Declare(http, id, displayName, keyPageUrl, modelsUrl);
	}

	/// <inheritdoc/>
	public string Id => Credential.Id;

	/// <inheritdoc/>
	public string Label => Credential.DisplayName;

	/// <inheritdoc/>
	public abstract string Detail { get; }

	/// <inheritdoc/>
	public CredentialDefinition Credential { get; }

	/// <inheritdoc/>
	public abstract Task<DecisionProviderResult> DecideAsync(DecisionProviderRequest request, CancellationToken ct);

	/// <summary>Declares a vendor key whose check is an authenticated GET of <paramref name="modelsUrl"/>.</summary>
	private static CredentialDefinition Declare(HttpClient http, string id, string displayName, string keyPageUrl, string modelsUrl) =>
		new() {
			Id = id,
			DisplayName = displayName,
			KeyNoun = "API key",
			KeyPageUrl = keyPageUrl,
			ValidateAsync = async (key, ct) => {
				using var request = Request(HttpMethod.Get, modelsUrl, key);
				using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
				if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) {
					throw new InvalidOperationException($"{displayName} rejected the key — make sure it's a valid API key.");
				}
				if (!response.IsSuccessStatusCode) {
					throw new InvalidOperationException($"{displayName} returned {(int)response.StatusCode} checking the key.");
				}
				return string.Empty;
			},
		};

	/// <summary>Posts <paramref name="body"/> with the saved key and reads the reply with <paramref name="read"/>.</summary>
	private protected async Task<DecisionProviderResult> PostAsync<TRequest, TResponse>(
		string url,
		TRequest body,
		JsonTypeInfo<TRequest> requestType,
		JsonTypeInfo<TResponse> responseType,
		Func<TResponse, string?, DecisionProviderResult> read,
		CancellationToken ct) {
		string name = Label;
		CredentialSecret? key;
		try {
			key = _store.Read(Id);
		} catch (InvalidOperationException ex) {
			return Failure(InferenceFailureKind.NotConfigured, $"{ex.Message} Connect {name} again to use it (run Connect Service…).", null);
		}
		if (key is null) {
			return Failure(InferenceFailureKind.NotConfigured, $"Connect {name} to use it (run Connect Service…).", null);
		}

		using var request = Request(HttpMethod.Post, url, key);
		request.Content = new StringContent(JsonSerializer.Serialize(body, requestType), Encoding.UTF8, "application/json");
		HttpResponseMessage? response = null;
		string payload;
		try {
			response = await _http.SendAsync(request, ct).ConfigureAwait(false);
			payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
		} catch (HttpRequestException) {
			response?.Dispose();
			return Failure(InferenceFailureKind.ProviderUnavailable, $"Couldn't reach {name}.", null);
		}

		using (response) {
			string? requestId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
			if (!response.IsSuccessStatusCode) {
				var (kind, detail) = Classify((int)response.StatusCode, name, ErrorMessage(payload));
				return Failure(kind, detail, requestId);
			}
			try {
				return JsonSerializer.Deserialize(payload, responseType) is { } decoded
					? read(decoded, requestId)
					: Missing(name, requestId);
			} catch (JsonException) {
				return Missing(name, requestId);
			}
		}
	}

	/// <summary>The failure for a reply that holds no answer to the <c>decision</c> question.</summary>
	private protected static DecisionProviderFailure Missing(string name, string? requestId) =>
		Failure(InferenceFailureKind.InvalidResponse, $"{name} returned no answer to the decision.", requestId);

	private static (InferenceFailureKind Kind, string Detail) Classify(int status, string name, string? message) => status switch {
		401 or 403 => (InferenceFailureKind.AuthenticationFailed, $"{name} rejected its key — run Connect Service… for {name}."),
		429 => (InferenceFailureKind.RateLimited, $"{name} rate-limited the decision."),
		400 or 413 or 422 => (InferenceFailureKind.InputRejected,
			$"{name} rejected the decision request{(message is null ? "." : $": {message}")}"),
		_ => (InferenceFailureKind.ProviderUnavailable, $"{name} returned HTTP {status}."),
	};

	// Only the vendor's own message field is copied (OpenAI's error.message, TypeSafe's string detail), never the body.
	private static string? ErrorMessage(string payload) {
		try {
			using var document = JsonDocument.Parse(payload);
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object) {
				return null;
			}
			if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
				&& error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String) {
				return message.GetString();
			}
			return root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString() : null;
		} catch (JsonException) {
			return null;
		}
	}

	private static DecisionProviderFailure Failure(InferenceFailureKind kind, string detail, string? requestId) =>
		new() { ModelId = string.Empty, RequestId = requestId, Kind = kind, Detail = detail };

	private static HttpRequestMessage Request(HttpMethod method, string url, CredentialSecret key) {
		var request = new HttpRequestMessage(method, url);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Reveal());
		return request;
	}
}
