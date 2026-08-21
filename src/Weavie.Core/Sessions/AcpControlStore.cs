using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Sessions;

/// <summary>Persists opaque ACP control defaults by provider.</summary>
public sealed class AcpControlStore {
	private const int Version = 1;
	private static readonly JsonSerializerOptions JsonOptions = new() {
		WriteIndented = true,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
	};
	private readonly IFileSystem _fileSystem;
	private readonly Lock _gate = new();
	private readonly AcpControlStoreException? _loadFailure;
	private Dictionary<string, Dictionary<string, string>> _providers;

	/// <summary>Creates and loads the store at <paramref name="path"/>.</summary>
	public AcpControlStore(IFileSystem fileSystem, string path) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(path);
		_fileSystem = fileSystem;
		FilePath = path;
		try {
			_providers = Load();
		} catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) {
			_providers = new(StringComparer.Ordinal);
			_loadFailure = new AcpControlStoreException($"ACP control defaults could not be loaded from '{path}': {ex.Message}", ex);
		}
	}

	/// <summary>The file backing this store.</summary>
	public string FilePath { get; }

	/// <summary>Returns one provider's remembered opaque control values.</summary>
	public IReadOnlyDictionary<string, string> Resolve(string providerId) {
		ArgumentException.ThrowIfNullOrEmpty(providerId);
		lock (_gate) {
			EnsureAvailable();
			return _providers.TryGetValue(providerId, out var values)
				? new Dictionary<string, string>(values, StringComparer.Ordinal)
				: new Dictionary<string, string>(StringComparer.Ordinal);
		}
	}

	/// <summary>Atomically remembers an accepted provider control value.</summary>
	public void Set(string providerId, string axis, string value) {
		ArgumentException.ThrowIfNullOrEmpty(providerId);
		ArgumentException.ThrowIfNullOrEmpty(axis);
		ArgumentException.ThrowIfNullOrEmpty(value);
		lock (_gate) {
			EnsureAvailable();
			var next = Clone();
			if (!next.TryGetValue(providerId, out var values)) next.Add(providerId, values = new(StringComparer.Ordinal));
			if (values.TryGetValue(axis, out string? current) && current == value) return;
			values[axis] = value;
			Persist(next);
			_providers = next;
		}
	}

	/// <summary>Atomically removes a stale provider control value.</summary>
	public void Clear(string providerId, string axis, string expectedValue) {
		ArgumentException.ThrowIfNullOrEmpty(providerId);
		ArgumentException.ThrowIfNullOrEmpty(axis);
		ArgumentException.ThrowIfNullOrEmpty(expectedValue);
		lock (_gate) {
			EnsureAvailable();
			var next = Clone();
			if (!next.TryGetValue(providerId, out var values)
				|| !values.TryGetValue(axis, out string? current)
				|| current != expectedValue) return;
			values.Remove(axis);
			if (values.Count == 0) next.Remove(providerId);
			Persist(next);
			_providers = next;
		}
	}

	private Dictionary<string, Dictionary<string, string>> Load() {
		if (!_fileSystem.FileExists(FilePath)) return new(StringComparer.Ordinal);
		using var probe = JsonDocument.Parse(_fileSystem.ReadAllText(FilePath));
		if (!probe.RootElement.TryGetProperty("version", out var format) || !format.TryGetInt32(out int version)) {
			throw new JsonException("The ACP control document requires a numeric version.");
		}
		if (version != Version) return new(StringComparer.Ordinal);
		var document = JsonSerializer.Deserialize<Document>(probe.RootElement.GetRawText(), JsonOptions)
			?? throw new JsonException("The ACP control document is empty.");
		var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
		foreach (var provider in document.Providers ?? throw new JsonException("The ACP control document requires providers.")) {
			if (string.IsNullOrEmpty(provider.Key) || provider.Value is null
				|| provider.Value.Any(entry => string.IsNullOrEmpty(entry.Key) || string.IsNullOrEmpty(entry.Value))) {
				throw new JsonException("ACP control providers, axes, and values must be non-empty.");
			}
			result.Add(provider.Key, new Dictionary<string, string>(provider.Value, StringComparer.Ordinal));
		}
		return result;
	}

	private void Persist(Dictionary<string, Dictionary<string, string>> providers) {
		try {
			_fileSystem.WriteAllTextAtomic(FilePath, JsonSerializer.Serialize(new Document { Version = Version, Providers = providers }, JsonOptions));
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			throw new AcpControlStoreException($"ACP control defaults could not be persisted to '{FilePath}': {ex.Message}", ex);
		}
	}

	private Dictionary<string, Dictionary<string, string>> Clone() => _providers.ToDictionary(
		entry => entry.Key,
		entry => new Dictionary<string, string>(entry.Value, StringComparer.Ordinal),
		StringComparer.Ordinal);

	private void EnsureAvailable() {
		if (_loadFailure is not null) throw _loadFailure;
	}

	private sealed class Document {
		[JsonPropertyName("version")]
		public int Version { get; set; }

		[JsonPropertyName("providers")]
		public Dictionary<string, Dictionary<string, string>>? Providers { get; set; }
	}
}

/// <summary>Reports an ACP control-default load or persistence failure without resetting its data.</summary>
public sealed class AcpControlStoreException : IOException {
	/// <summary>Creates a typed control-store failure.</summary>
	public AcpControlStoreException(string message, Exception innerException) : base(message, innerException) { }
}
