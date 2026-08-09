using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Sessions;

/// <summary>
/// The session rail's app-global UI state (<see cref="LastLocation"/>, <see cref="Promoted"/>, and
/// <see cref="Selected"/>), persisted atomically to <c>~/.weavie/rail-state.json</c>. Its own file, never
/// settings.toml — it's runtime UI state the host owns on the web's behalf, so it stays off the Claude-facing
/// settings surface. A malformed file is backed up to <c>rail-state.json.bad</c> and reset rather than throwing.
/// </summary>
public sealed class RailStateStore {
	private const string DefaultLocation = "local";
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	private readonly IFileSystem _fileSystem;
	private readonly Lock _gate = new();
	private string _lastLocation;
	private List<string> _promoted;
	private (string BackendId, string Slot)? _selected;

	/// <summary>Creates the store over <paramref name="path"/> (default <c>~/.weavie/rail-state.json</c>), loading it now.</summary>
	public RailStateStore(IFileSystem fileSystem, string? path) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		_fileSystem = fileSystem;
		FilePath = path ?? WeaviePaths.RailStateFile;
		lock (_gate) {
			var document = LoadLocked();
			_lastLocation = string.IsNullOrWhiteSpace(document.LastLocation) ? DefaultLocation : document.LastLocation;
			_promoted = [.. document.Promoted.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal)];
			_selected = document.Selected is { BackendId.Length: > 0, Slot.Length: > 0 } selected
				? (selected.BackendId, selected.Slot)
				: null;
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

	/// <summary>The promoted remote-session keys (<c>"backendId id"</c>). Snapshot copy; safe to enumerate.</summary>
	public IReadOnlyList<string> Promoted {
		get { lock (_gate) { return [.. _promoted]; } }
	}

	/// <summary>The last client-selected backend and stable session slot, or <c>null</c> before any selection.</summary>
	public (string BackendId, string Slot)? Selected {
		get { lock (_gate) { return _selected; } }
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

	/// <summary>Records the client-selected backend and stable session slot. No-op when unchanged or blank.</summary>
	public void SetSelected(string backendId, string slot) {
		if (string.IsNullOrWhiteSpace(backendId) || string.IsNullOrWhiteSpace(slot)) {
			return;
		}

		lock (_gate) {
			var next = (BackendId: backendId, Slot: slot);
			if (_selected == next) {
				return;
			}

			_selected = next;
			PersistLocked();
		}

		Changed?.Invoke();
	}

	private Document LoadLocked() => JsonStoreFile.Load(
		_fileSystem,
		FilePath,
		text => JsonSerializer.Deserialize<Document>(text) ?? new Document(),
		static () => new Document(),
		Log);

	private void PersistLocked() {
		var document = new Document {
			Version = 2,
			LastLocation = _lastLocation,
			Promoted = _promoted,
			Selected = _selected is { } selected
				? new SelectionEntry { BackendId = selected.BackendId, Slot = selected.Slot }
				: null,
		};
		JsonStoreFile.Persist(
			_fileSystem,
			FilePath,
			JsonSerializer.Serialize(document, JsonOptions),
			Log);
	}

	private sealed class Document {
		[JsonPropertyName("version")]
		public int Version { get; set; }

		[JsonPropertyName("lastLocation")]
		public string LastLocation { get; set; } = DefaultLocation;

		[JsonPropertyName("promoted")]
		public List<string> Promoted { get; set; } = [];

		[JsonPropertyName("selected")]
		public SelectionEntry? Selected { get; set; }
	}

	private sealed class SelectionEntry {
		[JsonPropertyName("backendId")]
		public string BackendId { get; set; } = string.Empty;

		[JsonPropertyName("slot")]
		public string Slot { get; set; } = string.Empty;
	}
}
