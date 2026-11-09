using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Weavie.Core.Credentials;

namespace Weavie.Core.Inference.Decisions;

/// <summary>
/// The one HTTP path every decision vendor shares: the bearer key (the only place a vendor key is revealed), one POST
/// with no retry, and the mapping from each failure to a kind whose detail never carries a raw body or the key.
/// </summary>
internal static class VendorHttp {
	/// <summary>Declares a vendor key whose check is an authenticated GET of <paramref name="modelsUrl"/>.</summary>
	public static CredentialDefinition Credential(HttpClient http, string id, string displayName, string keyPageUrl, string modelsUrl) =>
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
	public static async Task<DecisionProviderResult> DecideAsync<TRequest, TResponse>(
		HttpClient http,
		ICredentialStore store,
		CredentialDefinition credential,
		string url,
		TRequest body,
		JsonTypeInfo<TRequest> requestType,
		JsonTypeInfo<TResponse> responseType,
		Func<TResponse, string?, DecisionProviderResult> read,
		CancellationToken ct) {
		string name = credential.DisplayName;
		CredentialSecret? key;
		try {
			key = store.Read(credential.Id);
		} catch (InvalidOperationException) {
			key = null;
		}
		if (key is null) {
			return Failure(InferenceFailureKind.NotConfigured, $"Connect {name} to use it (run Connect Service…).", null);
		}

		using var request = Request(HttpMethod.Post, url, key);
		request.Content = new StringContent(JsonSerializer.Serialize(body, requestType), Encoding.UTF8, "application/json");
		HttpResponseMessage response;
		try {
			response = await http.SendAsync(request, ct).ConfigureAwait(false);
		} catch (HttpRequestException) {
			return Failure(InferenceFailureKind.ProviderUnavailable, $"Couldn't reach {name}.", null);
		}

		using (response) {
			string? requestId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
			string payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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
	public static DecisionProviderFailure Missing(string name, string? requestId) =>
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
