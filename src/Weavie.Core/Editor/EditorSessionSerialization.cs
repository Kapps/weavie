using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Editor;

/// <summary>
/// JSON (de)serialization for <see cref="EditorSession"/>: camelCase names, indented on disk. The host→web
/// restore push is built by <see cref="BuildRestoreJson"/>.
/// </summary>
public static partial class EditorSessionSerialization {
	/// <summary>Serializes a session to indented JSON (the on-disk form).</summary>
	public static string Serialize(EditorSession session) => JsonSerializer.Serialize(session, EditorDiskJson.Default.EditorSession);

	/// <summary>
	/// Parses a session. Returns <c>false</c> with an <paramref name="error"/> on malformed JSON rather than throwing.
	/// </summary>
	public static bool TryDeserialize(string json, out EditorSession? session, out string? error) {
		try {
			session = JsonSerializer.Deserialize(json, EditorDiskJson.Default.EditorSession);
			if (session is null) {
				error = "editor session document was empty";
				return false;
			}

			error = null;
			return true;
		} catch (JsonException ex) {
			session = null;
			error = ex.Message;
			return false;
		}
	}

	/// <summary>Builds the bridge restore payload at the host's editor revision, dropping files that no longer exist.</summary>
	public static string BuildRestoreJson(
		EditorSession session,
		long revision,
		IFileSystem fileSystem,
		Action<string> log) {
		ArgumentNullException.ThrowIfNull(session);
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentNullException.ThrowIfNull(log);

		var open = new List<EditorRestoreEntry>();
		var surviving = new HashSet<string>(StringComparer.Ordinal);
		foreach (var entry in session.Open) {
			if (entry.IsFile && !fileSystem.FileExists(entry.Path)) {
				log($"[editor-session] open file no longer exists; skipping {entry.Path}");
				continue;
			}

			surviving.Add(entry.Path);
			open.Add(new EditorRestoreEntry(entry.Path, entry.Kind, entry.ViewState, entry.Preview, entry.Pinned, entry.Scratch));
		}

		string? active = session.Active is { } path && surviving.Contains(path) ? path : null;
		return JsonSerializer.Serialize(
			new EditorRestore(new EditorRestoreSession(active, open, session.Review), revision),
			EditorMessageJson.Default.EditorRestore);
	}

	// On disk: camelCase, indented, nulls omitted.
	[JsonSourceGenerationOptions(
		PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
	[JsonSerializable(typeof(EditorSession))]
	private sealed partial class EditorDiskJson : JsonSerializerContext;

	// Bridge message: camelCase, single-line, nulls kept so active/viewState arrive explicitly.
	[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
	[JsonSerializable(typeof(EditorRestore))]
	private sealed partial class EditorMessageJson : JsonSerializerContext;

	private sealed record EditorRestore(EditorRestoreSession Session, long Revision);

	private sealed record EditorRestoreSession(string? Active, IReadOnlyList<EditorRestoreEntry> Open, JsonElement? Review);

	private sealed record EditorRestoreEntry(
		string Path,
		string? Kind,
		JsonElement? ViewState,
		bool Preview,
		bool Pinned,
		bool Scratch);
}
