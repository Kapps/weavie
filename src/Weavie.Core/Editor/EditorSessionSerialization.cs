using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weavie.Core.Editor;

/// <summary>
/// JSON (de)serialization for <see cref="EditorSession"/>: camelCase names, indented on disk. The host→web
/// restore push is built by <see cref="EditorSessionStore.BuildRestoreJson()"/>.
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
}
