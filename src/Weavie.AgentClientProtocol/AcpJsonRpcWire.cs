using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weavie.AgentClientProtocol;

/// <summary>Writes JSON-RPC 2.0 lines. Payloads are JSON DOM, so nothing reaches the wire through reflection.</summary>
internal static class AcpJsonRpcWire {
	public static string Request(long id, string method, JsonObject parameters) => Line(writer => {
		writer.WriteNumber("id", id);
		writer.WriteString("method", method);
		Write(writer, "params", parameters);
	});

	public static string Notification(string method, JsonObject parameters) => Line(writer => {
		writer.WriteString("method", method);
		Write(writer, "params", parameters);
	});

	public static string Result(JsonElement id, JsonNode? result) => Line(writer => {
		writer.WritePropertyName("id");
		id.WriteTo(writer);
		Write(writer, "result", result);
	});

	public static string Result(long id, JsonNode? result) => Line(writer => {
		writer.WriteNumber("id", id);
		Write(writer, "result", result);
	});

	public static string Error(JsonElement id, int code, string message, JsonNode? data) => Line(writer => {
		writer.WritePropertyName("id");
		id.WriteTo(writer);
		Write(writer, "error", new JsonObject { ["code"] = code, ["message"] = message, ["data"] = data });
	});

	public static string Error(long id, int code, string message) => Line(writer => {
		writer.WriteNumber("id", id);
		Write(writer, "error", new JsonObject { ["code"] = code, ["message"] = message });
	});

	private static void Write(Utf8JsonWriter writer, string name, JsonNode? value) {
		writer.WritePropertyName(name);
		if (value is null) writer.WriteNullValue();
		else value.WriteTo(writer);
	}

	private static string Line(Action<Utf8JsonWriter> body) {
		var buffer = new System.Buffers.ArrayBufferWriter<byte>();
		using (var writer = new Utf8JsonWriter(buffer)) {
			writer.WriteStartObject();
			writer.WriteString("jsonrpc", "2.0");
			body(writer);
			writer.WriteEndObject();
		}
		return Encoding.UTF8.GetString(buffer.WrittenSpan);
	}
}
