using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Sessions;

/// <summary>
/// The session rail's app-global UI state (<see cref="LastLocation"/>, <see cref="Selected"/>,
/// <see cref="PanelPinned"/>, <see cref="PanelWidth"/>, and <see cref="ParkedCollapsed"/>), persisted atomically to <c>~/.weavie/rail-state.json</c>. Its own file, never
/// settings.toml — it's runtime UI state the host owns on the web's behalf, so it stays off the Claude-facing
/// settings surface. A malformed file is backed up to <c>rail-state.json.bad</c> and reset rather than throwing.
/// </summary>
public sealed partial class RailStateStore : JsonDocumentStore {
	private const string DefaultLocation = "local";

	/// <summary>The pinned session panel's width before the user resizes it.</summary>
	public const int DefaultPanelWidth = 300;

	private string _lastLocation = DefaultLocation;
	private (string BackendId, string Slot)? _selected;
	private bool _panelPinned;
	private int _panelWidth = DefaultPanelWidth;
	private bool _parkedCollapsed = true;

	/// <summary>Creates the store over <paramref name="path"/> (default <c>~/.weavie/rail-state.json</c>), loading it now.</summary>
	/// <param name="fileSystem">The filesystem the state persists through.</param>
	/// <param name="path">The backing file, or <c>null</c> for the default.</param>
	public RailStateStore(IFileSystem fileSystem, string? path)
		: base(fileSystem, path ?? WeaviePaths.RailStateFile) {
		Load();
	}

	/// <summary>Raised (off the UI thread) after the state changes, so each window re-pushes it to its page.</summary>
	public event Action? Changed;

	/// <summary>The backend id the last session was created on (<c>local</c> by default).</summary>
	public string LastLocation {
		get { lock (Gate) { return _lastLocation; } }
	}

	/// <summary>Whether unloaded ("parked") sessions are folded away in the rail and session panel.</summary>
	public bool ParkedCollapsed {
		get { lock (Gate) { return _parkedCollapsed; } }
	}

	/// <summary>Folds or unfolds parked sessions. No-op (no write, no event) when unchanged.</summary>
	public void SetParkedCollapsed(bool collapsed) {
		lock (Gate) {
			if (_parkedCollapsed == collapsed) {
				return;
			}

			_parkedCollapsed = collapsed;
			PersistLocked();
		}

		Changed?.Invoke();
	}

	/// <summary>The last client-selected backend and stable session slot, or <c>null</c> before any selection.</summary>
	public (string BackendId, string Slot)? Selected {
		get { lock (Gate) { return _selected; } }
	}

	/// <summary>Whether the session panel is docked open beside the rail.</summary>
	public bool PanelPinned {
		get { lock (Gate) { return _panelPinned; } }
	}

	/// <summary>Docks or undocks the session panel. No-op (no write, no event) when unchanged.</summary>
	public void SetPanelPinned(bool pinned) {
		lock (Gate) {
			if (_panelPinned == pinned) {
				return;
			}

			_panelPinned = pinned;
			PersistLocked();
		}

		Changed?.Invoke();
	}

	/// <summary>The pinned session panel's width in CSS pixels.</summary>
	public int PanelWidth {
		get { lock (Gate) { return _panelWidth; } }
	}

	/// <summary>Records the pinned panel's resized width. No-op (no write, no event) when unchanged.</summary>
	public void SetPanelWidth(int width) {
		lock (Gate) {
			if (_panelWidth == width) {
				return;
			}

			_panelWidth = width;
			PersistLocked();
		}

		Changed?.Invoke();
	}

	/// <summary>Records the backend a session was just created on. No-op (no write, no event) when unchanged.</summary>
	public void SetLastLocation(string location) {
		string next = string.IsNullOrWhiteSpace(location) ? DefaultLocation : location;
		lock (Gate) {
			if (string.Equals(_lastLocation, next, StringComparison.Ordinal)) {
				return;
			}

			_lastLocation = next;
			PersistLocked();
		}

		Changed?.Invoke();
	}

	/// <summary>Records the client-selected backend and stable session slot. No-op when unchanged or blank.</summary>
	public void SetSelected(string backendId, string slot) {
		if (string.IsNullOrWhiteSpace(backendId) || string.IsNullOrWhiteSpace(slot)) {
			return;
		}

		lock (Gate) {
			var next = (BackendId: backendId, Slot: slot);
			if (_selected == next) {
				return;
			}

			_selected = next;
			PersistLocked();
		}

		Changed?.Invoke();
	}

	/// <inheritdoc/>
	protected override void Restore(string? text) {
		var document = (text is null ? null : JsonSerializer.Deserialize(text, RailStateStoreJson.Default.Document)) ?? new Document();
		_lastLocation = string.IsNullOrWhiteSpace(document.LastLocation) ? DefaultLocation : document.LastLocation;
		_selected = document.Selected is { BackendId.Length: > 0, Slot.Length: > 0 } selected
			? (selected.BackendId, selected.Slot)
			: null;
		_panelPinned = document.PanelPinned;
		_panelWidth = document.PanelWidth;
		_parkedCollapsed = document.ParkedCollapsed;
	}

	/// <inheritdoc/>
	protected override string Render() => JsonSerializer.Serialize(
		new Document {
			Version = 2,
			LastLocation = _lastLocation,
			Selected = _selected is { } selected
				? new SelectionEntry { BackendId = selected.BackendId, Slot = selected.Slot }
				: null,
			PanelPinned = _panelPinned,
			PanelWidth = _panelWidth,
			ParkedCollapsed = _parkedCollapsed,
		},
		RailStateStoreJson.Default.Document);

	private sealed class Document {
		[JsonPropertyName("version")]
		public int Version { get; set; }

		[JsonPropertyName("lastLocation")]
		public string LastLocation { get; set; } = DefaultLocation;

		[JsonPropertyName("selected")]
		public SelectionEntry? Selected { get; set; }

		[JsonPropertyName("panelPinned")]
		public bool PanelPinned { get; set; }

		[JsonPropertyName("panelWidth")]
		public int PanelWidth { get; set; } = DefaultPanelWidth;

		[JsonPropertyName("parkedCollapsed")]
		public bool ParkedCollapsed { get; set; } = true;
	}

	private sealed class SelectionEntry {
		[JsonPropertyName("backendId")]
		public string BackendId { get; set; } = string.Empty;

		[JsonPropertyName("slot")]
		public string Slot { get; set; } = string.Empty;
	}

	[JsonSourceGenerationOptions(WriteIndented = true)]
	[JsonSerializable(typeof(Document))]
	private sealed partial class RailStateStoreJson : JsonSerializerContext;
}
