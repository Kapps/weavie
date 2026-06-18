using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weavie.Core.Editor;

/// <summary>
/// One open editor entry: a file <see cref="Path"/> plus its opaque Monaco <see cref="ViewState"/>
/// (scroll + cursor + folding, the JSON from <c>editor.saveViewState()</c>). The view state is stored and
/// forwarded verbatim — Weavie never interprets it; it only hands it back to <c>editor.restoreViewState</c>.
/// File contents are deliberately absent — disk is the source of truth: the web reopens each file as a
/// working copy resolved from disk through the host file provider, so no buffer is persisted or pushed here.
/// </summary>
public sealed record EditorSessionEntry {
	/// <summary>The native absolute path of the open file.</summary>
	public required string Path { get; init; }

	/// <summary>Opaque Monaco view state (scroll/cursor/folding), or <c>null</c> when none was captured.</summary>
	public JsonElement? ViewState { get; init; }
}

/// <summary>
/// The persisted editor session for one workspace (<c>~/.weavie/workspaces/&lt;id&gt;/editor-session.json</c>):
/// the list of <see cref="Open"/> files (list-shaped so it extends to tabs) and which one is
/// <see cref="Active"/>. Sibling of <see cref="ActiveEditor"/> — that tracks the <em>live</em> editor for
/// the MCP server; this persists the editor session across launches. Unknown top-level fields (e.g.
/// written by a newer build) round-trip via <see cref="Extra"/>. See <c>docs/specs/editor-session.md</c>.
/// </summary>
public sealed record EditorSession {
	/// <summary>The path of the file currently shown in the editor, or <c>null</c> when none is open.</summary>
	public string? Active { get; init; }

	/// <summary>The open files, in order; the only one restored eagerly today is <see cref="Active"/>.</summary>
	public IReadOnlyList<EditorSessionEntry> Open { get; init; } = [];

	/// <summary>Unknown top-level fields, preserved verbatim across a load/save round-trip.</summary>
	[JsonExtensionData]
	public IDictionary<string, JsonElement>? Extra { get; init; }

	/// <summary>The empty session: nothing open, no active file. Used on first run and after a reset.</summary>
	public static EditorSession Empty { get; } = new();
}
