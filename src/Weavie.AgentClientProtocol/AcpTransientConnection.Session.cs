using System.Text.Json;
using System.Text.Json.Nodes;
using Weavie.Core.Agents;
using Weavie.Core.Inference;
using Weavie.Core.Json;

namespace Weavie.AgentClientProtocol;

internal sealed partial class AcpTransientConnection {
	private JsonElement _setup;
	private string SessionId => RequiredString(_setup, "sessionId");

	internal static JsonObject InitializeParameters() => new() {
		["protocolVersion"] = 1,
		["clientCapabilities"] = new JsonObject { ["session"] = SessionCapabilities() },
		["clientInfo"] = ClientInfo("1"),
	};

	internal static JsonObject SessionCapabilities() =>
		new() { ["configOptions"] = new JsonObject { ["boolean"] = new JsonObject() } };

	internal static JsonObject ClientInfo(string version) =>
		new() { ["name"] = "weavie", ["title"] = "Weavie", ["version"] = version };

	public Task<JsonElement> InitializeAsync(CancellationToken ct) => RequestAsync("initialize", InitializeParameters(), ct);

	public async Task<JsonElement> OpenSessionAsync(string workspace, CancellationToken ct) {
		_setup = await RequestAsync("session/new", AcpContent.Session(Path.GetFullPath(workspace), []), ct).ConfigureAwait(false);
		_ = SessionId;
		return _setup;
	}

	public Task<IReadOnlyList<AgentControlAxis>> ApplyProfileAsync(InferenceProviderProfile profile, CancellationToken ct) =>
		AcpProfile.ApplyAsync(this, SessionId, _setup, profile, ct);

	public async Task<(string StopReason, JsonElement Response)> PromptAsync(JsonArray prompt, CancellationToken ct) {
		var response = await RequestAsync("session/prompt", new JsonObject {
			["sessionId"] = SessionId,
			["prompt"] = prompt,
		}, ct).ConfigureAwait(false);
		return (RequiredString(response, "stopReason"), response);
	}

	public void ListenReply(Action<long, string, JsonElement> onRequest, Action<string> append, Action toolCall) =>
		Listen(onRequest, (method, parameters) => {
			if (method != "session/update" || parameters.ValueKind != JsonValueKind.Object
				|| !parameters.TryGetProperty("update", out var update)) return;
			switch (update.GetStringOrNull("sessionUpdate")) {
				case "agent_message_chunk" when update.TryGetProperty("content", out var content):
					if (content.GetStringOrNull("text") is { } text) append(text);
					break;
				case "tool_call": toolCall(); break;
			}
		});

	private static string RequiredString(JsonElement value, string property) =>
		value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String
			&& result.GetString() is { Length: > 0 } text
				? text
				: throw new AcpProtocolException($"The ACP response is missing '{property}'.");
}
