using System.Text.Json;

namespace Weavie.AcpDistribution;

/// <summary>Reads and validates the official ACP Registry index.</summary>
public sealed class AcpRegistryClient {
	/// <summary>The canonical current registry index.</summary>
	public static Uri OfficialIndex { get; } = new(
		"https://cdn.agentclientprotocol.com/registry/v1/latest/registry.json",
		UriKind.Absolute);

	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	private readonly HttpClient _http;
	private readonly Uri _index;

	/// <summary>Creates a client for the official registry.</summary>
	public AcpRegistryClient(HttpClient http) : this(http, OfficialIndex) { }

	internal AcpRegistryClient(HttpClient http, Uri index) {
		ArgumentNullException.ThrowIfNull(http);
		ArgumentNullException.ThrowIfNull(index);
		if (!index.IsAbsoluteUri || index.Scheme != Uri.UriSchemeHttps && index.Scheme != Uri.UriSchemeHttp) {
			throw new ArgumentException("The ACP Registry index must be an absolute HTTP(S) URL.", nameof(index));
		}
		_http = http;
		_index = index;
	}

	internal async Task<IReadOnlyList<AcpRegistryEntry>> FetchAsync(CancellationToken ct) {
		using var response = await _http.GetAsync(_index, HttpCompletionOption.ResponseHeadersRead, ct)
			.ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
		var document = await JsonSerializer.DeserializeAsync<AcpRegistryDocument>(stream, JsonOptions, ct)
			.ConfigureAwait(false)
			?? throw new JsonException("The ACP Registry index is empty.");
		if (document.Version != "1.0.0") {
			throw new JsonException($"Unsupported ACP Registry format '{document.Version ?? "missing"}'.");
		}
		if (document.Agents is null) throw new JsonException("The ACP Registry index requires an agents array.");
		var ids = new HashSet<string>(StringComparer.Ordinal);
		var result = new List<AcpRegistryEntry>(document.Agents.Length);
		foreach (var candidate in document.Agents) {
			var agent = candidate ?? throw new JsonException("The ACP Registry cannot contain null agents.");
			string id = RequireSegment(agent.Id, "agent id", allowPlus: false);
			if (!ids.Add(id)) throw new JsonException($"The ACP Registry repeats agent '{id}'.");
			Require(agent.Name, $"agent '{id}' name");
			RequireSegment(agent.Version, $"agent '{id}' version", allowPlus: true);
			if (agent.Distribution is null) throw new JsonException($"Agent '{id}' has no distribution.");
			result.Add(agent);
		}
		return result;
	}

	internal static string Require(string? value, string field) =>
		!string.IsNullOrWhiteSpace(value) ? value : throw new JsonException($"The ACP Registry {field} is missing.");

	internal static string RequireSegment(string? value, string field, bool allowPlus) {
		string segment = Require(value, field);
		if (!char.IsAsciiLetterOrDigit(segment[0])
			|| segment.Any(character => !char.IsAsciiLetterOrDigit(character)
				&& character is not '.' and not '_' and not '-'
				&& (!allowPlus || character != '+'))) {
			throw new JsonException($"The ACP Registry {field} is not a safe path segment.");
		}
		return segment;
	}
}
