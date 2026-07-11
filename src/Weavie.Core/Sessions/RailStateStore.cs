using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Sessions;

/// <summary>
/// The session rail's app-global UI state (<see cref="LastLocation"/> and <see cref="Promoted"/>), persisted
/// atomically to <c>~/.weavie/rail-state.json</c>. Its own file, never settings.toml — it's runtime UI state
/// the host owns on the web's behalf, so it stays off the Claude-facing settings surface. A malformed file is
/// backed up to <c>rail-state.json.bad</c> and reset rather than throwing.
/// </summary>
public sealed class RailStateStore {
	private const string DefaultLocation = "local";
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	private readonly IFileSystem _fileSystem;
	private readonly Lock _gate = new();
	private string _lastLocation;
	private string? _lastAgentProvider;
	private List<string> _promoted;

	/// <summary>Creates the store over <paramref name="path"/> (default <c>~/.weavie/rail-state.json</c>), loading it now.</summary>
	public RailStateStore(IFileSystem fileSystem, string? path) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		_fileSystem = fileSystem;
		FilePath = path ?? WeaviePaths.RailStateFile;
		lock (_gate) {
			var document = LoadLocked();
			_lastLocation = string.IsNullOrWhiteSpace(document.LastLocation) ? DefaultLocation : document.LastLocation;
			_lastAgentProvider = NormalizeAgentProvider(document.LastAgentProvider);
			_promoted = [.. document.Promoted.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal)];
		}
	}

	/// <summary>Raised (off the UI thread) after the state changes, so each window re-pushes it to its page.</summary>
	public event Action? Changed;

	/// <summary>Diagnostic log line — read failures, malformed-file resets, persist failures.</summary>
	public event Action<string>? Log;

	/// <summary>The rail-state file backing this store.</summary>
	public string FilePath { get; }

	/// <summary>The backend id the last session was created on (<c>local</c> by default).</summary>
	public string LastLocation {
		get { lock (_gate) { return _lastLocation; } }
	}

	/// <summary>The agent provider used for the last newly-created session, or null before one is remembered.</summary>
	public string? LastAgentProvider {
		get { lock (_gate) { return _lastAgentProvider; } }
	}

	/// <summary>The promoted remote-session keys (<c>"backendId id"</c>). Snapshot copy; safe to enumerate.</summary>
	public IReadOnlyList<string> Promoted {
		get { lock (_gate) { return [.. _promoted]; } }
	}

	/// <summary>Records the backend a session was just created on. No-op (no write, no event) when unchanged.</summary>
	public void SetLastLocation(string location) {
		string next = string.IsNullOrWhiteSpace(location) ? DefaultLocation : location;
		lock (_gate) {
			if (string.Equals(_lastLocation, next, StringComparison.Ordinal)) {
				return;
			}

			_lastLocation = next;
			PersistLocked();
		}

		Changed?.Invoke();
	}

	/// <summary>Records the agent provider used for a newly-created session.</summary>
	public void SetLastAgentProvider(string provider) {
		string? next = NormalizeAgentProvider(provider);
		lock (_gate) {
			if (string.Equals(_lastAgentProvider, next, StringComparison.Ordinal)) {
				return;
			}

			_lastAgentProvider = next;
			PersistLocked();
		}

		Changed?.Invoke();
	}

	/// <summary>Replaces the promoted set with <paramref name="keys"/>. No-op (no write, no event) when unchanged.</summary>
	public void SetPromoted(IEnumerable<string> keys) {
		ArgumentNullException.ThrowIfNull(keys);
		var next = keys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal).ToList();
		lock (_gate) {
			if (next.Count == _promoted.Count && next.All(_promoted.Contains)) {
				return;
			}

			_promoted = next;
			PersistLocked();
		}

		Changed?.Invoke();
	}

	private Document LoadLocked() {
		if (!_fileSystem.FileExists(FilePath)) {
			return new Document();
		}

		string text;
		try {
			text = _fileSystem.ReadAllText(FilePath);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[rail-state] could not read {FilePath}: {ex.Message}; starting empty");
			return new Document();
		}

		try {
			return JsonSerializer.Deserialize<Document>(text) ?? new Document();
		} catch (JsonException ex) {
			Log?.Invoke($"[rail-state] {FilePath} is malformed ({ex.Message}); backing up to rail-state.json.bad and resetting");
			JsonStoreFile.BackupBad(_fileSystem, FilePath, text, "rail-state", Log);
			return new Document();
		}
	}

	private void PersistLocked() {
		try {
			var document = new Document { Version = 1, LastLocation = _lastLocation, LastAgentProvider = _lastAgentProvider, Promoted = _promoted };
			_fileSystem.WriteAllTextAtomic(FilePath, JsonSerializer.Serialize(document, JsonOptions));
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[rail-state] could not persist: {ex.Message}");
		}
	}

	private static string? NormalizeAgentProvider(string? provider) => provider?.Trim() switch {
		"claude" => "claude",
		"codex" => "codex",
		_ => null,
	};

	private sealed class Document {
		[JsonPropertyName("version")]
		public int Version { get; set; }

		[JsonPropertyName("lastLocation")]
		public string LastLocation { get; set; } = DefaultLocation;

		[JsonPropertyName("lastAgentProvider")]
		public string? LastAgentProvider { get; set; }

		[JsonPropertyName("promoted")]
		public List<string> Promoted { get; set; } = [];
	}
}
