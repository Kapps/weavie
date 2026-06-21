using System.Text;
using System.Text.Json;

namespace Weavie.Core.Json;

/// <summary>
/// Builds a JSON string by writing to a transient <see cref="Utf8JsonWriter"/>, sparing every caller the
/// <c>MemoryStream</c>/<c>Utf8JsonWriter</c>/<c>Encoding.UTF8.GetString</c> boilerplate.
/// </summary>
public static class JsonWrite {
	/// <summary>Runs <paramref name="body"/> against a fresh writer and returns the written UTF-8 text.</summary>
	public static string ToText(Action<Utf8JsonWriter> body) {
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream)) {
			body(writer);
		}

		return Encoding.UTF8.GetString(stream.ToArray());
	}

	/// <summary>Wraps <paramref name="body"/> in a root object: <c>{ … }</c>.</summary>
	public static string Object(Action<Utf8JsonWriter> body) =>
		ToText(writer => {
			writer.WriteStartObject();
			body(writer);
			writer.WriteEndObject();
		});
}
