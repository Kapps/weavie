using System.Text.Json;
using Weavie.Core.Agents;

namespace Weavie.Hosting.Agents.Codex;

/// <summary>Builds Codex app-server launch configuration for Weavie's capability registry.</summary>
internal static class CodexAppServerConfig {
	public static IReadOnlyList<string> Arguments(AgentSessionContext context) {
		ArgumentNullException.ThrowIfNull(context);
		string bearer = "Bearer " + context.Registry.Credential.Token;
		return [
			"-c", "mcp_servers.weavie.enabled=true",
			"-c", "mcp_servers.weavie.required=true",
			"-c", "mcp_servers.weavie.url=" + TomlString(context.Registry.StreamableHttpUrl),
			"-c", "mcp_servers.weavie.http_headers.Authorization=" + TomlString(bearer),
		];
	}

	private static string TomlString(string value) => JsonSerializer.Serialize(value);
}
