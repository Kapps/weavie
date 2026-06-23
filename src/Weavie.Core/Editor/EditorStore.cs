using System.Text.Json;

namespace Weavie.Core.Editor;

/// <summary>A 0-based position in a document (line + character), matching the IDE selection wire protocol.</summary>
public readonly record struct EditorPosition(int Line, int Character);

/// <summary>The active editor's selection: a 0-based range plus whether it is empty (caret only, no text selected).</summary>
public readonly record struct EditorSelection(EditorPosition Start, EditorPosition End, bool IsEmpty);

/// <summary>The editor the user is currently looking at: file, language id, selection, and selected text.</summary>
public sealed record ActiveEditor(string FilePath, string? LanguageId, string SelectedText, EditorSelection Selection) {
	/// <summary>
	/// Parses an <c>active-editor-changed</c> message, converting the file URI to a native path. Returns
	/// <c>false</c> for a message without a usable file URI (e.g. an in-memory model).
	/// </summary>
	public static bool TryParse(JsonElement message, out ActiveEditor? editor) {
		editor = null;
		if (message.ValueKind != JsonValueKind.Object
			|| !message.TryGetProperty("uri", out var uriElement)
			|| uriElement.GetString() is not { Length: > 0 } uri
			|| !TryUriToPath(uri, out string? path)
			|| path is null) {
			return false;
		}

		string? languageId = message.TryGetProperty("languageId", out var langElement) ? langElement.GetString() : null;
		string text = message.TryGetProperty("text", out var textElement) ? textElement.GetString() ?? string.Empty : string.Empty;
		editor = new ActiveEditor(path, languageId, text, ParseSelection(message));
		return true;
	}

	private static EditorSelection ParseSelection(JsonElement message) {
		if (!message.TryGetProperty("selection", out var selection) || selection.ValueKind != JsonValueKind.Object) {
			return new EditorSelection(default, default, IsEmpty: true);
		}

		var start = ParsePosition(selection, "start");
		var end = ParsePosition(selection, "end");
		bool isEmpty = selection.TryGetProperty("isEmpty", out var emptyElement)
			&& emptyElement.ValueKind is JsonValueKind.True or JsonValueKind.False
				? emptyElement.GetBoolean()
				: start == end;
		return new EditorSelection(start, end, isEmpty);
	}

	private static EditorPosition ParsePosition(JsonElement selection, string name) {
		if (!selection.TryGetProperty(name, out var position) || position.ValueKind != JsonValueKind.Object) {
			return default;
		}

		int line = position.TryGetProperty("line", out var l) && l.TryGetInt32(out int li) ? li : 0;
		int character = position.TryGetProperty("character", out var c) && c.TryGetInt32(out int ci) ? ci : 0;
		return new EditorPosition(line, character);
	}

	private static bool TryUriToPath(string uri, out string? path) {
		path = null;
		try {
			var parsed = new Uri(uri);
			if (!parsed.IsFile) {
				return false;
			}

			path = parsed.LocalPath;
			return true;
		} catch (UriFormatException) {
			return false;
		}
	}
}

/// <summary>
/// One open editor tab (via <c>open-editors-changed</c>): <see cref="FilePath"/> plus active/pinned/preview
/// flags. Backs the MCP <c>getOpenEditors</c> tool. <see cref="FilePath"/> is the web's tab key (a native
/// path), echoed back verbatim by <c>close_tab</c> so the page can match it exactly.
/// </summary>
public sealed record OpenEditorTab(string FilePath, bool IsActive, bool IsPinned, bool IsPreview) {
	/// <summary>Parses the <c>editors</c> array into tabs (skipping entries without a path); empty for a malformed message.</summary>
	public static IReadOnlyList<OpenEditorTab> ParseList(JsonElement message) {
		if (message.ValueKind != JsonValueKind.Object
			|| !message.TryGetProperty("editors", out var editors)
			|| editors.ValueKind != JsonValueKind.Array) {
			return [];
		}

		var list = new List<OpenEditorTab>(editors.GetArrayLength());
		foreach (var entry in editors.EnumerateArray()) {
			if (entry.ValueKind != JsonValueKind.Object
				|| !entry.TryGetProperty("path", out var pathElement)
				|| pathElement.GetString() is not { Length: > 0 } path) {
				continue;
			}

			list.Add(new OpenEditorTab(path, ReadBool(entry, "isActive"), ReadBool(entry, "isPinned"), ReadBool(entry, "isPreview")));
		}

		return list;
	}

	private static bool ReadBool(JsonElement entry, string name) =>
		entry.TryGetProperty(name, out var value)
		&& value.ValueKind is JsonValueKind.True or JsonValueKind.False
		&& value.GetBoolean();
}

/// <summary>
/// Per-session editor-state hub: tracks the active editor and open tabs so the IDE-MCP server can tell the
/// embedded Claude what the user is working with. The server reads <see cref="Active"/> for
/// <c>getCurrentSelection</c>, <see cref="OpenEditors"/> for <c>getOpenEditors</c>, and pushes
/// <c>selection_changed</c> on <see cref="Changed"/>.
/// </summary>
public sealed class EditorStore {
	// Immutable snapshot references: volatile suffices (reference reads/writes are atomic), letting the MCP
	// task read Active while the host thread writes it without a lock.
	private volatile ActiveEditor? _active;
	private volatile IReadOnlyList<OpenEditorTab> _openEditors = [];

	/// <summary>The editor the user is currently looking at, or <c>null</c> until the page reports one.</summary>
	public ActiveEditor? Active => _active;

	/// <summary>The full set of open editor tabs, in display order; empty until the page reports them.</summary>
	public IReadOnlyList<OpenEditorTab> OpenEditors => _openEditors;

	/// <summary>Raised when the active file or selection changes, so the server can notify Claude.</summary>
	public event Action<ActiveEditor>? Changed;

	/// <summary>Records the editor the user is now looking at and notifies subscribers.</summary>
	public void SetActive(ActiveEditor editor) {
		ArgumentNullException.ThrowIfNull(editor);
		_active = editor;
		Changed?.Invoke(editor);
	}

	/// <summary>
	/// Records the open tabs. Pull-based, so it doesn't raise <see cref="Changed"/> (which exists only to push
	/// <c>selection_changed</c>); <c>getOpenEditors</c> reads <see cref="OpenEditors"/> on demand.
	/// </summary>
	public void SetOpenEditors(IReadOnlyList<OpenEditorTab> editors) {
		ArgumentNullException.ThrowIfNull(editors);
		_openEditors = editors;
	}

	/// <summary>
	/// Drops the active file + open-tab mirror when the session leaves the foreground, so a backgrounded
	/// Claude isn't told the user is looking at a file they switched away from. Deliberately does NOT raise
	/// <see cref="Changed"/> — a backgrounded session needs no <c>selection_changed</c> push.
	/// </summary>
	public void Clear() {
		_active = null;
		_openEditors = [];
	}
}
