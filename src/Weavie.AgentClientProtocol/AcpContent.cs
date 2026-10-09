using System.Text.Json.Nodes;

namespace Weavie.AgentClientProtocol;

/// <summary>Builds the ACP prompt content blocks and shared request fragments Weavie sends.</summary>
internal static class AcpContent {
	public static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };

	public static JsonObject Image(string mimeType, string data) =>
		new() { ["type"] = "image", ["mimeType"] = mimeType, ["data"] = data };

	/// <summary>Text addressed only to the assistant, never shown as the user's words.</summary>
	public static JsonObject AssistantText(string text) => new() {
		["type"] = "text",
		["text"] = text,
		["annotations"] = AssistantAudience(),
	};

	/// <summary>An embedded plain-text resource addressed only to the assistant.</summary>
	public static JsonObject AssistantResource(string uri, string text) => new() {
		["type"] = "resource",
		["annotations"] = AssistantAudience(),
		["resource"] = new JsonObject { ["uri"] = uri, ["mimeType"] = "text/plain", ["text"] = text },
	};

	/// <summary>The session/new or session/load parameters for <paramref name="cwd"/> with <paramref name="mcpServers"/>.</summary>
	public static JsonObject Session(string cwd, JsonArray mcpServers) => new() { ["cwd"] = cwd, ["mcpServers"] = mcpServers };

	/// <summary>The permission outcome that selects <paramref name="optionId"/>.</summary>
	public static JsonObject Selected(string optionId) =>
		new() { ["outcome"] = new JsonObject { ["outcome"] = "selected", ["optionId"] = optionId } };

	/// <summary>The permission outcome for a cancelled prompt.</summary>
	public static JsonObject Cancelled() => new() { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } };

	private static JsonObject AssistantAudience() => new() { ["audience"] = new JsonArray("assistant") };
}
