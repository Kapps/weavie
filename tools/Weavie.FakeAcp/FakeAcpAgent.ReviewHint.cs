using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weavie.FakeAcp;

// `review-hint:{"path":…,"line":…,"severity":…,"text":…}` calls Weavie's addReviewHint over the session's HTTP MCP
// server, as a real ACP agent would, and reports the tool's reply as a message.
internal sealed partial class FakeAcpAgent {
	private const string ReviewHintPrefix = "review-hint:";
	private (string Url, string Authorization)? _weavieMcp;

	private async Task AddReviewHintAsync(string argumentsJson, CancellationToken ct) {
		var (url, authorization) = _weavieMcp ?? throw new InvalidOperationException("No Weavie MCP server was offered.");
		var request = new JsonObject {
			["jsonrpc"] = "2.0",
			["id"] = 1,
			["method"] = "tools/call",
			["params"] = new JsonObject { ["name"] = "addReviewHint", ["arguments"] = JsonNode.Parse(argumentsJson) },
		};
		using var client = new HttpClient();
		using var message = new HttpRequestMessage(HttpMethod.Post, url) {
			Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
		};
		message.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization);
		using var response = await client.SendAsync(message, ct).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		using var reply = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
		var result = reply.RootElement.GetProperty("result");
		string text = result.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
		Message((result.TryGetProperty("isError", out var error) && error.GetBoolean() ? "review hint failed: " : "review hint: ") + text);
	}
}
