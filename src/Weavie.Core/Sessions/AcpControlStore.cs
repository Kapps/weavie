using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Sessions;

/// <summary>
/// Persists opaque ACP control defaults by provider. Unlike the config stores, an unusable file is never reset
/// or backed up: the failure is held and rethrown from the first use, so a provider never silently loses the
/// controls the user accepted.
/// </summary>
public sealed class AcpControlStore : JsonDocumentStore {
	private const int Version = 1;
	private static readonly JsonSerializerOptions JsonOptions = new() {
		WriteIndented = true,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
	};

	private AcpControlStoreException? _loadFailure;
	private Dictionary<string, Dictionary<string, string>> _providers = new(StringComparer.Ordinal);

	/// <summary>Creates and loads the store at <paramref name="path"/>.</summary>
	/// <param name="fileSystem">The filesystem the defaults persist through.</param>
	/// <param name="path">The backing file.</param>
	public AcpControlStore(IFileSystem fileSystem, string path) : base(fileSystem, path) {
		Load();
	}

	/// <summary>Returns one provider's remembered opaque control values.</summary>
	public IReadOnlyDictionary<string, string> Resolve(string providerId) {
		ArgumentException.ThrowIfNullOrEmpty(providerId);
		lock (Gate) {
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
		lock (Gate) {
			EnsureAvailable();
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
			EnsureAvailable();
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
	protected override void Restore(string? text) {
		_providers = new(StringComparer.Ordinal);
		if (text is null) return;
		using var probe = JsonDocument.Parse(text);
		if (!probe.RootElement.TryGetProperty("version", out var format) || !format.TryGetInt32(out int version)) {
			throw new JsonException("The ACP control document requires a numeric version.");
		}
		if (version != Version) return;
		var document = JsonSerializer.Deserialize<Document>(probe.RootElement.GetRawText(), JsonOptions)
			?? throw new JsonException("The ACP control document is empty.");
		foreach (var provider in document.Providers ?? throw new JsonException("The ACP control document requires providers.")) {
			if (string.IsNullOrEmpty(provider.Key) || provider.Value is null
				|| provider.Value.Any(entry => string.IsNullOrEmpty(entry.Key) || string.IsNullOrEmpty(entry.Value))) {
				throw new JsonException("ACP control providers, axes, and values must be non-empty.");
			}
			_providers.Add(provider.Key, new Dictionary<string, string>(provider.Value, StringComparer.Ordinal));
		}
	}

	/// <inheritdoc/>
	protected override string Render() =>
		JsonSerializer.Serialize(new Document { Version = Version, Providers = _providers }, JsonOptions);

	/// <inheritdoc/>
	protected override void OnUnusable(string? text, Exception cause) {
		ArgumentNullException.ThrowIfNull(cause);
		Restore(null);
		_loadFailure = new AcpControlStoreException($"ACP control defaults could not be loaded from '{FilePath}': {cause.Message}", cause);
	}

	/// <inheritdoc/>
	protected override void OnPersistFailed(Exception cause) {
		ArgumentNullException.ThrowIfNull(cause);
		throw new AcpControlStoreException($"ACP control defaults could not be persisted to '{FilePath}': {cause.Message}", cause);
	}

	// The store only adopts state a write already accepted, so a failed persist leaves memory and disk agreeing.
	private void Commit(Dictionary<string, Dictionary<string, string>> next) {
		var previous = _providers;
		_providers = next;
		try {
			PersistLocked();
		} catch (AcpControlStoreException) {
			_providers = previous;
			throw;
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
