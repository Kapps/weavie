using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weavie.FakeAcp;

// `mcp:<tool>:<arguments json>` calls that tool on Weavie's session HTTP MCP server, as a real ACP agent would, and
// reports the tool's reply as a message: `<tool>: <reply>`, or `<tool> failed: <reply>`.
internal sealed partial class FakeAcpAgent {
	private const string WeavieToolPrefix = "mcp:";
	private (string Url, string Authorization)? _weavieMcp;

	private async Task CallWeavieToolAsync(string call, CancellationToken ct) {
		int colon = call.IndexOf(':', StringComparison.Ordinal);
		string tool = call[..colon];
		var (text, failed) = await PostWeavieToolAsync(tool, call[(colon + 1)..], ct).ConfigureAwait(false);
		Message((failed ? $"{tool} failed: " : $"{tool}: ") + text);
	}

	private async Task<(string Text, bool Failed)> PostWeavieToolAsync(string tool, string argumentsJson, CancellationToken ct) {
		var (url, authorization) = _weavieMcp ?? throw new InvalidOperationException("No Weavie MCP server was offered.");
		var request = new JsonObject {
			["jsonrpc"] = "2.0",
			["id"] = 1,
			["method"] = "tools/call",
			["params"] = new JsonObject { ["name"] = tool, ["arguments"] = JsonNode.Parse(argumentsJson) },
		};
		using var client = new HttpClient();
		using var message = new HttpRequestMessage(HttpMethod.Post, url) {
			Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
		};
		message.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization);
		using var response = await client.SendAsync(message, ct).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		// The reply is an event stream; its last event is the call's response.
		string events = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
		string data = events.Split('\n').Last(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..];
		using var reply = JsonDocument.Parse(data);
		var result = reply.RootElement.GetProperty("result");
		string text = result.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
		return (text, result.TryGetProperty("isError", out var error) && error.GetBoolean());
	}
}
