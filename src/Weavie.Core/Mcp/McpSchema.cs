using System.Text.Json.Nodes;

namespace Weavie.Core.Mcp;

// Builders for the JSON schemas of tools whose arguments are checked by Core's own rules.
internal static class McpSchema {
	public static JsonObject Text() => new() { ["type"] = "string" };

	public static JsonObject Integer() => new() { ["type"] = "integer", ["minimum"] = 1 };

	public static JsonObject Enum(IReadOnlyList<string> values) =>
		new() { ["type"] = "string", ["enum"] = new JsonArray([.. values.Select(value => (JsonNode)value)]) };

	public static JsonObject Described(JsonObject schema, string description) {
		schema["description"] = description;
		return schema;
	}

	public static JsonObject Array(JsonNode items) => new() { ["type"] = "array", ["items"] = items };

	public static JsonObject Object(JsonObject properties, params string[] required) => new() {
		["type"] = "object",
		["properties"] = properties,
		["required"] = new JsonArray([.. required.Select(name => (JsonNode)name)]),
	};

	public static JsonObject Tool(string name, string description, JsonObject schema) =>
		new() { ["name"] = name, ["description"] = description, ["inputSchema"] = schema };
}
