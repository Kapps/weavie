using System.Text.Json;

namespace Weavie.AgentClientProtocol;

internal static class AcpContentAnnotations {
	internal static bool IsUserVisible(JsonElement content) {
		if (!content.TryGetProperty("annotations", out var annotations) || annotations.ValueKind == JsonValueKind.Null) return true;
		if (annotations.ValueKind != JsonValueKind.Object) {
			throw new AcpProtocolException("ACP content annotations must be an object.");
		}
		if (!annotations.TryGetProperty("audience", out var audience) || audience.ValueKind == JsonValueKind.Null) return true;
		if (audience.ValueKind != JsonValueKind.Array) {
			throw new AcpProtocolException("ACP content audience must be an array of roles.");
		}
		bool visible = false;
		foreach (var role in audience.EnumerateArray()) {
			if (role.ValueKind != JsonValueKind.String || role.GetString() is not ("user" or "assistant")) {
				throw new AcpProtocolException("ACP content audience roles must be 'user' or 'assistant'.");
			}
			visible |= role.GetString() == "user";
		}
		return visible;
	}
}
