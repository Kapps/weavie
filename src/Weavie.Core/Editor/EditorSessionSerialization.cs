using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Editor;

/// <summary>
/// JSON (de)serialization for <see cref="EditorSession"/>: camelCase names, indented on disk. The host→web
/// restore push is built by <see cref="BuildRestoreJson"/>.
/// </summary>
public static class EditorSessionSerialization {
	/// <summary>On-disk options: camelCase, indented, nulls omitted.</summary>
	public static JsonSerializerOptions Options { get; } = new() {
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	/// <summary>
	/// Bridge-message options: camelCase, single-line. Nulls kept so <c>active</c>/<c>viewState</c> are emitted
	/// explicitly rather than dropped to undefined.
	/// </summary>
	public static JsonSerializerOptions MessageOptions { get; } = new() {
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	/// <summary>Serializes a session to indented JSON (the on-disk form).</summary>
	public static string Serialize(EditorSession session) => JsonSerializer.Serialize(session, Options);

	/// <summary>
	/// Parses a session. Returns <c>false</c> with an <paramref name="error"/> on malformed JSON rather than throwing.
	/// </summary>
	public static bool TryDeserialize(string json, out EditorSession? session, out string? error) {
		try {
			session = JsonSerializer.Deserialize<EditorSession>(json, Options);
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

	/// <summary>Builds the bridge restore payload, dropping files that no longer exist.</summary>
	public static string BuildRestoreJson(
		EditorSession session,
		IFileSystem fileSystem,
		Action<string> log) {
		ArgumentNullException.ThrowIfNull(session);
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentNullException.ThrowIfNull(log);

		var open = new List<object>();
		var surviving = new HashSet<string>(StringComparer.Ordinal);
		foreach (var entry in session.Open) {
			if (entry.IsFile && !fileSystem.FileExists(entry.Path)) {
				log($"[editor-session] open file no longer exists; skipping {entry.Path}");
				continue;
			}

			surviving.Add(entry.Path);
			open.Add(new {
				path = entry.Path,
				kind = entry.Kind,
				viewState = entry.ViewState,
				preview = entry.Preview,
				pinned = entry.Pinned,
				scratch = entry.Scratch,
			});
		}

		string? active = session.Active is { } path && surviving.Contains(path) ? path : null;
		return JsonSerializer.Serialize(new { session = new { active, open } }, MessageOptions);
	}
}
