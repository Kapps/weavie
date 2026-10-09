using System.Text.Json.Serialization.Metadata;
using Weavie.Core.Editor;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

public sealed partial class HostSession {
	private readonly Lock _editorSessionGate = new();
	private EditorSession _editorSession = EditorSession.Empty;
	// Stamps every host edit; a page snapshot whose basis is older has not applied it and would erase it.
	private long _editorRevision = 1;

	/// <summary>
	/// This session's open editor tabs (paths + opaque view state), in memory for the window's lifetime. Host edits
	/// and page snapshots both write it; a snapshot applies only once the page has applied every host edit.
	/// </summary>
	public EditorSession EditorSession {
		get { lock (_editorSessionGate) { return _editorSession; } }
	}

	internal event Action<EditorSession>? EditorSessionChanged;

	/// <summary>Installs the slot's saved tabs before any page can address this session.</summary>
	internal void SeedEditorSession(EditorSession session) {
		ArgumentNullException.ThrowIfNull(session);
		lock (_editorSessionGate) Commit(session);
	}

	/// <summary>Replaces the tabs with a page snapshot, unless it was taken before the latest host edit.</summary>
	internal bool ApplyPageSnapshot(EditorSession session, long basis) {
		ArgumentNullException.ThrowIfNull(session);
		lock (_editorSessionGate) {
			if (basis < _editorRevision) return false;
			Commit(session);
			return true;
		}
	}

	internal void ReplayEditor(MessageTargetFeature target, Action<string> log) {
		ArgumentNullException.ThrowIfNull(target);
		ArgumentNullException.ThrowIfNull(log);
		lock (_editorSessionGate) {
			target.PublishJson(
				"restore",
				EditorSessionSerialization.BuildRestoreJson(_editorSession, _editorRevision, FileSystem, log));
		}
	}

	internal void OpenEditorOverlay(string path, string kind) => PublishEditorEdit(
		current => WithOpen(current, path, preview: false, scratch: false, kind),
		"openOverlay",
		WireJson.Default.EditorOverlayOpened,
		revision => new(path, kind, revision));

	private void PublishEditorFileOpen(string path, int? line, bool preview, bool scratch, EditorOpenIntent intent) =>
		PublishEditorEdit(
			current => WithOpen(current, path, preview, scratch, kind: null),
			"openFile",
			WireJson.Default.EditorFileOpened,
			revision => new(path, line, preview, scratch, intent == EditorOpenIntent.Reveal ? "reveal" : "navigation", revision));

	private void PublishEditorClose(string path) => PublishEditorEdit(
		current => WithClosed(current, path),
		"closeTab",
		WireJson.Default.EditorTabClosed,
		revision => new(path, revision));

	private void PublishEditorEdit<T>(
		Func<EditorSession, EditorSession?> edit,
		string name,
		JsonTypeInfo<T> type,
		Func<long, T> payload) {
		lock (_editorSessionGate) {
			if (edit(_editorSession) is not { } next) return;
			_editorSession = next;
			_editorMessages.Publish(name, type, payload(++_editorRevision));
			EditorSessionChanged?.Invoke(next);
		}
	}

	// Raised under the gate so observers see the edits in the order they were applied.
	private void Commit(EditorSession next) {
		_editorSession = next;
		EditorSessionChanged?.Invoke(next);
	}

	private static EditorSession WithOpen(EditorSession current, string path, bool preview, bool scratch, string? kind) {
		var open = current.Open.ToList();
		int existing = open.FindIndex(entry => SameEditorPath(entry.Path, path));
		if (existing >= 0) {
			var entry = open[existing];
			bool sameKind = (entry.Kind ?? "file") == (kind ?? "file");
			open[existing] = entry with {
				Kind = kind,
				Preview = entry.Preview && preview,
				ViewState = sameKind ? entry.ViewState : null,
			};
		} else {
			var entry = new EditorSessionEntry {
				Path = path,
				Kind = kind,
				ViewState = null,
				Preview = preview,
				Scratch = scratch,
			};
			int priorPreview = preview ? open.FindIndex(candidate => candidate.Preview) : -1;
			if (priorPreview >= 0) {
				open[priorPreview] = entry;
			} else {
				open.Add(entry);
			}
		}

		return current with { Active = path, Open = open };
	}

	private static EditorSession? WithClosed(EditorSession current, string path) {
		int index = current.Open.ToList().FindIndex(entry => SameEditorPath(entry.Path, path));
		if (index < 0) return null;
		var open = current.Open.Where(entry => !SameEditorPath(entry.Path, path)).ToArray();
		string? active = current.Active;
		if (active is not null && SameEditorPath(active, path)) {
			active = open.Length == 0 ? null : open[Math.Min(index, open.Length - 1)].Path;
		}

		return current with { Active = active, Open = open };
	}

	private static bool SameEditorPath(string left, string right) =>
		string.Equals(
			left,
			right,
			OperatingSystem.IsWindows()
				? StringComparison.OrdinalIgnoreCase
				: StringComparison.Ordinal);
}

internal sealed record EditorOverlayOpened(string Path, string Kind, long Revision);

internal sealed record EditorFileOpened(string Path, int? Line, bool Preview, bool Scratch, string Intent, long Revision);

internal sealed record EditorTabClosed(string Path, long Revision);
