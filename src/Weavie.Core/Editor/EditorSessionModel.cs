using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weavie.Core.Editor;

/// <summary>
/// One open editor entry: a file <see cref="Path"/> plus its opaque Monaco <see cref="ViewState"/>, forwarded
/// verbatim. No file contents — disk is the source of truth and the web reopens each file as a working copy.
/// </summary>
public sealed record EditorSessionEntry {
	/// <summary>The native absolute path of the open file.</summary>
	public required string Path { get; init; }

	/// <summary>Opaque Monaco view state (scroll/cursor/folding), or <c>null</c> when none was captured.</summary>
	public JsonElement? ViewState { get; init; }

	/// <summary>A preview tab (italic, reused by the next preview open). Omitted from disk when false.</summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public bool Preview { get; init; }

	/// <summary>A pinned tab (compact, furthest-left, survives bulk-close). Omitted from disk when false.</summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public bool Pinned { get; init; }

	/// <summary>
	/// A scratch (untitled) buffer backed by a temp file in the scratch dir (see <see cref="ScratchStore"/>).
	/// Round-trips so a restored scratch tab keeps its identity. Omitted from disk when false.
	/// </summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public bool Scratch { get; init; }
}

/// <summary>
/// The editor session persisted per workspace (<c>~/.weavie/workspaces/&lt;id&gt;/editor-session.json</c>):
/// <see cref="Open"/> files and which is <see cref="Active"/>. Distinct from <see cref="ActiveEditor"/> (the
/// <em>live</em> editor for the MCP server). See <c>docs/specs/editor-session.md</c>.
/// </summary>
public sealed record EditorSession {
	/// <summary>The path of the file currently shown in the editor, or <c>null</c> when none is open.</summary>
	public string? Active { get; init; }

	/// <summary>The open files, in order; the only one restored eagerly is <see cref="Active"/>.</summary>
	public IReadOnlyList<EditorSessionEntry> Open { get; init; } = [];

	/// <summary>Unknown top-level fields, preserved verbatim across a load/save round-trip.</summary>
	[JsonExtensionData]
	public IDictionary<string, JsonElement>? Extra { get; init; }

	/// <summary>The empty session: nothing open, no active file. Used on first run and after a reset.</summary>
	public static EditorSession Empty { get; } = new();
}
