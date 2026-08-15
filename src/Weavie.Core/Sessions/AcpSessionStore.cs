using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Sessions;

/// <summary>Persists the ACP conversation associated with each provider and working directory.</summary>
public sealed class AcpSessionStore {
	private const int Version = 2;
	private static readonly JsonSerializerOptions JsonOptions = new() {
		WriteIndented = true,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
	};
	private readonly IFileSystem _fileSystem;
	private readonly Lock _gate = new();
	private readonly AcpSessionStoreException? _loadFailure;
	private List<Entry> _items;

	/// <summary>Creates and loads the store at <paramref name="path"/>.</summary>
	public AcpSessionStore(IFileSystem fileSystem, string path) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(path);
		_fileSystem = fileSystem;
		FilePath = path;
		try {
			_items = Load();
		} catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) {
			_items = [];
			_loadFailure = new AcpSessionStoreException(
				$"ACP session associations could not be loaded from '{path}': {ex.Message}",
				ex);
		}
	}

	/// <summary>The file backing this store.</summary>
	public string FilePath { get; }

	/// <summary>Returns the provider session associated with the working directory, when any.</summary>
	public string? Resolve(string providerId, string workingDirectory) {
		ArgumentException.ThrowIfNullOrEmpty(providerId);
		ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
		string cwd = Normalize(workingDirectory);
		lock (_gate) {
			EnsureAvailable();
			return _items.FirstOrDefault(item =>
				string.Equals(item.ProviderId, providerId, StringComparison.Ordinal)
				&& PathEquals(item.Cwd, cwd))?.SessionId;
		}
	}

	/// <summary>Returns the last locally allocated turn number for one provider conversation.</summary>
	public long ResolveTurnNumber(string providerId, string workingDirectory) {
		ArgumentException.ThrowIfNullOrEmpty(providerId);
		ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
		string cwd = Normalize(workingDirectory);
		lock (_gate) {
			EnsureAvailable();
			return _items.FirstOrDefault(item =>
				string.Equals(item.ProviderId, providerId, StringComparison.Ordinal)
				&& PathEquals(item.Cwd, cwd))?.TurnNumber ?? 0;
		}
	}

	/// <summary>Atomically associates a provider session with the working directory.</summary>
	public void Adopt(string providerId, string workingDirectory, string sessionId, long turnNumber) {
		ArgumentException.ThrowIfNullOrEmpty(providerId);
		ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
		ArgumentException.ThrowIfNullOrEmpty(sessionId);
		ArgumentOutOfRangeException.ThrowIfNegative(turnNumber);
		string cwd = Normalize(workingDirectory);
		lock (_gate) {
			EnsureAvailable();
			var next = CloneItems();
			var existing = next.FirstOrDefault(item =>
				string.Equals(item.ProviderId, providerId, StringComparison.Ordinal)
				&& PathEquals(item.Cwd, cwd));
			if (existing is null) {
				next.Add(new Entry {
					ProviderId = providerId,
					Cwd = cwd,
					SessionId = sessionId,
					TurnNumber = turnNumber,
				});
			} else if (string.Equals(existing.SessionId, sessionId, StringComparison.Ordinal)
				&& existing.TurnNumber == turnNumber) {
				return;
			} else {
				existing.SessionId = sessionId;
				existing.TurnNumber = turnNumber;
			}
			Persist(next);
			_items = next;
		}
	}

	/// <summary>Forgets the exact provider association for the working directory.</summary>
	public void Clear(string providerId, string workingDirectory) {
		ArgumentException.ThrowIfNullOrEmpty(providerId);
		ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
		string cwd = Normalize(workingDirectory);
		lock (_gate) {
			EnsureAvailable();
			var next = CloneItems();
			if (next.RemoveAll(item =>
				string.Equals(item.ProviderId, providerId, StringComparison.Ordinal)
				&& PathEquals(item.Cwd, cwd)) > 0) {
				Persist(next);
				_items = next;
			}
		}
	}

	private List<Entry> Load() => !_fileSystem.FileExists(FilePath)
		? []
		: Deserialize(_fileSystem.ReadAllText(FilePath));

	private static List<Entry> Deserialize(string text) {
		using var probe = JsonDocument.Parse(text);
		if (probe.RootElement.ValueKind != JsonValueKind.Object
			|| !probe.RootElement.TryGetProperty("version", out var format)
			|| !format.TryGetInt32(out int version)) {
			throw new JsonException("The ACP session document requires a numeric version.");
		}
		// Weavie carries no document migrations, so another generation's associations are unreadable here and
		// the next write takes the file over. Malformed data at this version still fails without a reset.
		if (version != Version) return [];
		var document = JsonSerializer.Deserialize<Document>(text, JsonOptions)
			?? throw new JsonException("The ACP session document is empty.");
		if (document.Sessions is null) {
			throw new JsonException("The ACP session document requires a sessions array.");
		}
		var identities = new HashSet<string>(StringComparer.Ordinal);
		var result = new List<Entry>();
		foreach (var item in document.Sessions) {
			if (item is null) {
				throw new JsonException("ACP session documents cannot contain null entries.");
			}
			if (string.IsNullOrEmpty(item.ProviderId)
				|| string.IsNullOrEmpty(item.Cwd)
				|| string.IsNullOrEmpty(item.SessionId)
				|| item.TurnNumber is not >= 0) {
				throw new JsonException("Every ACP session requires providerId, cwd, sessionId, and turnNumber.");
			}
			string cwd;
			try {
				cwd = Normalize(item.Cwd);
			} catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
				throw new JsonException("An ACP session cwd is invalid.", ex);
			}
			string identity = item.ProviderId + "\0" + (OperatingSystem.IsWindows() ? cwd.ToUpperInvariant() : cwd);
			if (!identities.Add(identity)) {
				throw new JsonException("The ACP session document contains a duplicate provider and cwd.");
			}
			result.Add(new Entry {
				ProviderId = item.ProviderId,
				Cwd = cwd,
				SessionId = item.SessionId,
				TurnNumber = item.TurnNumber.Value,
			});
		}
		return result;
	}

	private void Persist(IReadOnlyList<Entry> entries) {
		var document = new Document {
			Version = Version,
			Sessions = [.. entries.Select(item => new StoredEntry {
				ProviderId = item.ProviderId,
				Cwd = item.Cwd,
				SessionId = item.SessionId,
				TurnNumber = item.TurnNumber,
			})],
		};
		try {
			_fileSystem.WriteAllTextAtomic(FilePath, JsonSerializer.Serialize(document, JsonOptions));
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			throw new AcpSessionStoreException(
				$"ACP session associations could not be persisted to '{FilePath}': {ex.Message}",
				ex);
		}
	}

	private void EnsureAvailable() {
		if (_loadFailure is not null) throw _loadFailure;
	}

	private List<Entry> CloneItems() => [.. _items.Select(item => new Entry {
		ProviderId = item.ProviderId,
		Cwd = item.Cwd,
		SessionId = item.SessionId,
		TurnNumber = item.TurnNumber,
	})];

	private static string Normalize(string path) =>
		Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

	private static bool PathEquals(string left, string right) =>
		string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

	private sealed class Entry {
		public required string ProviderId { get; init; }
		public required string Cwd { get; init; }
		public required string SessionId { get; set; }
		public required long TurnNumber { get; set; }
	}

	private sealed class Document {
		[JsonPropertyName("version")]
		public int Version { get; set; }

		[JsonPropertyName("sessions")]
		public List<StoredEntry?>? Sessions { get; set; }
	}

	private sealed class StoredEntry {
		[JsonPropertyName("providerId")]
		public string? ProviderId { get; set; }

		[JsonPropertyName("cwd")]
		public string? Cwd { get; set; }

		[JsonPropertyName("sessionId")]
		public string? SessionId { get; set; }

		[JsonPropertyName("turnNumber")]
		public long? TurnNumber { get; set; }
	}
}

/// <summary>Reports an ACP session-association load or persistence failure without resetting its data.</summary>
public sealed class AcpSessionStoreException : IOException {
	/// <summary>Creates a typed association-store failure.</summary>
	public AcpSessionStoreException(string message, Exception innerException) : base(message, innerException) { }
}
