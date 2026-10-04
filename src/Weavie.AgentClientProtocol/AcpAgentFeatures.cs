using System.Text.Json;
using static Weavie.AgentClientProtocol.AcpJson;

namespace Weavie.AgentClientProtocol;

/// <summary>What one initialized ACP process generation advertised.</summary>
internal sealed record AcpAgentFeatures(
	bool Load,
	bool Fork,
	bool Resume,
	bool Close,
	bool Images,
	bool EmbeddedContext,
	bool HttpMcp,
	bool Steering,
	IReadOnlyList<AcpAuthMethod> AuthMethods) {
	/// <summary>The features of a process that has not initialized.</summary>
	public static AcpAgentFeatures None { get; } = new(false, false, false, false, false, false, false, false, []);

	/// <summary>Reads the advertised features from an <c>initialize</c> result.</summary>
	public static AcpAgentFeatures Read(JsonElement initialized) {
		var capabilities = AcpCapabilities.Read(initialized);
		return new(
			AcpCapabilities.Boolean(capabilities, "loadSession"),
			AcpCapabilities.HasObject(capabilities, "sessionCapabilities", "fork"),
			AcpCapabilities.HasObject(capabilities, "sessionCapabilities", "resume"),
			AcpCapabilities.HasObject(capabilities, "sessionCapabilities", "close"),
			AcpCapabilities.Boolean(capabilities, "promptCapabilities", "image"),
			AcpCapabilities.Boolean(capabilities, "promptCapabilities", "embeddedContext"),
			AcpCapabilities.Boolean(capabilities, "mcpCapabilities", "http"),
			initialized.TryGetProperty("_meta", out var meta) && AcpCapabilities.Boolean(meta, "steering", "supported"),
			ReadAuthMethods(initialized));
	}

	private static List<AcpAuthMethod> ReadAuthMethods(JsonElement initialized) {
		if (!initialized.TryGetProperty("authMethods", out var methods) || methods.ValueKind == JsonValueKind.Null) {
			return [];
		}
		if (methods.ValueKind != JsonValueKind.Array) {
			throw new AcpProtocolException("ACP authMethods must be an array when present.");
		}
		var ids = new HashSet<string>(StringComparer.Ordinal);
		var parsed = new List<AcpAuthMethod>();
		foreach (var method in methods.EnumerateArray()) {
			if (method.ValueKind != JsonValueKind.Object
				|| OptionalString(method, "id") is not { Length: > 0 } id
				|| OptionalString(method, "name") is not { Length: > 0 } name) continue;
			if (!ids.Add(id)) throw new AcpProtocolException($"ACP repeated auth method '{id}'.");
			string type = OptionalString(method, "type") ?? "agent";
			if (type is not ("agent" or "terminal")) continue;
			parsed.Add(new AcpAuthMethod(
				id,
				name,
				OptionalString(method, "description"),
				type,
				ReadAuthArguments(method),
				ReadAuthEnvironment(method)));
		}
		return parsed;
	}

	private static List<string> ReadAuthArguments(JsonElement method) {
		if (!method.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Array) return [];
		var result = new List<string>();
		foreach (var argument in args.EnumerateArray()) {
			if (argument.ValueKind != JsonValueKind.String) return [];
			result.Add(argument.GetString()!);
		}
		return result;
	}

	private static Dictionary<string, string> ReadAuthEnvironment(JsonElement method) {
		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		if (!method.TryGetProperty("env", out var environment) || environment.ValueKind != JsonValueKind.Object) {
			return result;
		}
		foreach (var entry in environment.EnumerateObject()) {
			if (entry.Name.Length == 0 || entry.Value.ValueKind != JsonValueKind.String) {
				return new Dictionary<string, string>(StringComparer.Ordinal);
			}
			result.Add(entry.Name, entry.Value.GetString()!);
		}
		return result;
	}
}

/// <summary>One authentication method an ACP agent advertised.</summary>
internal sealed record AcpAuthMethod(
	string Id,
	string Name,
	string? Description,
	string Type,
	IReadOnlyList<string> Arguments,
	IReadOnlyDictionary<string, string> Environment);
