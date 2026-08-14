namespace Weavie.AgentClientProtocol;

internal static class McpProxyBinary {
	public static string PathIn(string directory) {
		ArgumentException.ThrowIfNullOrEmpty(directory);
		string name = OperatingSystem.IsWindows() ? "weavie-mcp-proxy.exe" : "weavie-mcp-proxy";
		string path = Path.Combine(directory, name);
		return File.Exists(path)
			? path
			: throw new FileNotFoundException(
				$"The bundled MCP stdio proxy is missing: {path}",
				path);
	}
}
