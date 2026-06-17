using System.Text.Json;

namespace Weavie.Core.Editor;

/// <summary>A 0-based position in a document (line + character), matching the IDE selection wire protocol.</summary>
public readonly record struct EditorPosition(int Line, int Character);

/// <summary>The active editor's selection: a 0-based range plus whether it is empty (caret only, no text selected).</summary>
public readonly record struct EditorSelection(EditorPosition Start, EditorPosition End, bool IsEmpty);

/// <summary>
/// The editor the user is currently looking at: its file, language id, current selection, and the
/// selected text (empty when the selection is just a caret).
/// </summary>
public sealed record ActiveEditor(string FilePath, string? LanguageId, string SelectedText, EditorSelection Selection) {
	/// <summary>
	/// Parses an <c>active-editor-changed</c> bridge message into an <see cref="ActiveEditor"/>,
	/// converting the model's file URI to a native absolute path. Returns <c>false</c> for a message
	/// without a usable file URI (e.g. an in-memory / scratch model), in which case nothing is reported.
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
/// Per-session editor-state hub: tracks which editor the user is looking at so the IDE-MCP server can
/// tell the embedded Claude what the user is working with. The web editor pushes changes over the bridge
/// (<c>active-editor-changed</c>); the server reads <see cref="Active"/> for
/// <c>getCurrentSelection</c>/<c>getOpenEditors</c> and reacts to <see cref="Changed"/> by pushing a
/// <c>selection_changed</c> notification. Sibling of <c>LayoutStore</c>; a tabbed model can later add the
/// full set of open editors alongside the active one.
/// </summary>
public sealed class EditorStore {
	// A single immutable snapshot reference: volatile is enough (reference reads/writes are atomic) and
	// lets the MCP accept task read Active while the host thread writes it, with no lock.
	private volatile ActiveEditor? _active;

	/// <summary>The editor the user is currently looking at, or <c>null</c> until the page reports one.</summary>
	public ActiveEditor? Active => _active;

	/// <summary>Raised when the active file or selection changes, so the server can notify Claude.</summary>
	public event Action<ActiveEditor>? Changed;

	/// <summary>Records the editor the user is now looking at and notifies subscribers.</summary>
	public void SetActive(ActiveEditor editor) {
		ArgumentNullException.ThrowIfNull(editor);
		_active = editor;
		Changed?.Invoke(editor);
	}
}
