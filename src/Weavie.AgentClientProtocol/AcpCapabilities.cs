using System.Text.Json;

namespace Weavie.AgentClientProtocol;

internal static class AcpCapabilities {
	public static JsonElement Read(JsonElement initialized) {
		if (!initialized.TryGetProperty("protocolVersion", out var version)
			|| !version.TryGetInt32(out int protocolVersion)
			|| protocolVersion != 1) {
			throw new AcpProtocolException("The ACP agent did not negotiate stable protocol version 1.");
		}
		if (!initialized.TryGetProperty("agentCapabilities", out var capabilities)
			|| capabilities.ValueKind == JsonValueKind.Null) {
			return default;
		}
		if (capabilities.ValueKind != JsonValueKind.Object) {
			throw new AcpProtocolException("ACP agentCapabilities must be an object when present.");
		}
		return capabilities;
	}

	public static bool Boolean(JsonElement parent, string child, string property) =>
		parent.ValueKind == JsonValueKind.Object
		&& parent.TryGetProperty(child, out var value) && Boolean(value, property);

	public static bool Boolean(JsonElement parent, string property) =>
		parent.ValueKind == JsonValueKind.Object
		&& parent.TryGetProperty(property, out var value)
		&& value.ValueKind is JsonValueKind.True or JsonValueKind.False
		&& value.GetBoolean();

	public static bool HasObject(JsonElement parent, string child, string property) =>
		parent.ValueKind == JsonValueKind.Object
		&& parent.TryGetProperty(child, out var value)
		&& value.ValueKind == JsonValueKind.Object
		&& value.TryGetProperty(property, out var result)
		&& result.ValueKind == JsonValueKind.Object;
}
