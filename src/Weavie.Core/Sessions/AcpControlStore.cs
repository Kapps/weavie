using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Sessions;

/// <summary>
/// Persists opaque ACP control defaults by provider. An unusable file is never reset or backed up, so a provider never
/// silently loses the controls the user accepted.
/// </summary>
public sealed partial class AcpControlStore : StrictJsonDocumentStore<Dictionary<string, Dictionary<string, string>>> {
	private const int Version = 1;

	/// <summary>Creates and loads the store at <paramref name="path"/>.</summary>
	/// <param name="fileSystem">The filesystem the defaults persist through.</param>
	/// <param name="path">The backing file.</param>
	public AcpControlStore(IFileSystem fileSystem, string path) : base(fileSystem, path) {
		Load();
	}

	/// <inheritdoc/>
	protected override string Subject => "ACP control defaults";

	/// <summary>Returns one provider's remembered opaque control values.</summary>
	public IReadOnlyDictionary<string, string> Resolve(string providerId) {
		ArgumentException.ThrowIfNullOrEmpty(providerId);
		lock (Gate) {
			return Current.TryGetValue(providerId, out var values)
				? new Dictionary<string, string>(values, StringComparer.Ordinal)
				: new Dictionary<string, string>(StringComparer.Ordinal);
		}
	}

	/// <summary>Atomically remembers an accepted provider control value.</summary>
	public void Set(string providerId, string axis, string value) {
		ArgumentException.ThrowIfNullOrEmpty(providerId);
		ArgumentException.ThrowIfNullOrEmpty(axis);
		ArgumentException.ThrowIfNullOrEmpty(value);
		lock (Gate) {
			var next = Clone();
			if (!next.TryGetValue(providerId, out var values)) next.Add(providerId, values = new(StringComparer.Ordinal));
			if (values.TryGetValue(axis, out string? current) && current == value) return;
			values[axis] = value;
			Commit(next);
		}
	}

	/// <summary>Atomically removes a stale provider control value.</summary>
	public void Clear(string providerId, string axis, string expectedValue) {
		ArgumentException.ThrowIfNullOrEmpty(providerId);
		ArgumentException.ThrowIfNullOrEmpty(axis);
		ArgumentException.ThrowIfNullOrEmpty(expectedValue);
		lock (Gate) {
			var next = Clone();
			if (!next.TryGetValue(providerId, out var values)
				|| !values.TryGetValue(axis, out string? current)
				|| current != expectedValue) return;
			values.Remove(axis);
			if (values.Count == 0) next.Remove(providerId);
			Commit(next);
		}
	}

	/// <inheritdoc/>
	protected override Dictionary<string, Dictionary<string, string>> Empty() => new(StringComparer.Ordinal);

	/// <inheritdoc/>
	protected override Dictionary<string, Dictionary<string, string>> Read(string text) {
		var providers = Empty();
		using var probe = JsonDocument.Parse(text);
		if (!probe.RootElement.TryGetProperty("version", out var format) || !format.TryGetInt32(out int version)) {
			throw new JsonException("The ACP control document requires a numeric version.");
		}
		if (version != Version) return providers;
		var document = JsonSerializer.Deserialize(probe.RootElement.GetRawText(), AcpControlStoreJson.Default.Document)
			?? throw new JsonException("The ACP control document is empty.");
		foreach (var provider in document.Providers ?? throw new JsonException("The ACP control document requires providers.")) {
			if (string.IsNullOrEmpty(provider.Key) || provider.Value is null
				|| provider.Value.Any(entry => string.IsNullOrEmpty(entry.Key) || string.IsNullOrEmpty(entry.Value))) {
				throw new JsonException("ACP control providers, axes, and values must be non-empty.");
			}
			providers.Add(provider.Key, new Dictionary<string, string>(provider.Value, StringComparer.Ordinal));
		}
		return providers;
	}

	/// <inheritdoc/>
	protected override string Write(Dictionary<string, Dictionary<string, string>> state) =>
		JsonSerializer.Serialize(new Document { Version = Version, Providers = state }, AcpControlStoreJson.Default.Document);

	/// <inheritdoc/>
	protected override Exception Failure(string message, Exception cause) => new AcpControlStoreException(message, cause);

	private Dictionary<string, Dictionary<string, string>> Clone() => Current.ToDictionary(
		entry => entry.Key,
		entry => new Dictionary<string, string>(entry.Value, StringComparer.Ordinal),
		StringComparer.Ordinal);

	private sealed class Document {
		[JsonPropertyName("version")]
		public int Version { get; set; }

		[JsonPropertyName("providers")]
		public Dictionary<string, Dictionary<string, string>>? Providers { get; set; }
	}

	[JsonSourceGenerationOptions(WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
	[JsonSerializable(typeof(Document))]
	private sealed partial class AcpControlStoreJson : JsonSerializerContext;
}

/// <summary>Reports an ACP control-default load or persistence failure without resetting its data.</summary>
public sealed class AcpControlStoreException : IOException {
	/// <summary>Creates a typed control-store failure.</summary>
	public AcpControlStoreException(string message, Exception innerException) : base(message, innerException) { }
}
