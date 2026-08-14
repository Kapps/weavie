using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weavie.FakeAcp;

/// <summary>Handles the agent-facing half of one ACP connection.</summary>
public interface IAcpAgent : IAsyncDisposable {
	/// <summary>Faults when the provider runtime becomes unusable and the ACP connection must end.</summary>
	Task TerminalFailure { get; }

	/// <summary>Attaches the connection before the first message is dispatched.</summary>
	void Attach(AcpAgentConnection connection);

	/// <summary>Handles one client request.</summary>
	Task<JsonNode> HandleRequestAsync(
		JsonElement requestId,
		string method,
		JsonElement parameters,
		CancellationToken ct);

	/// <summary>Handles one client notification.</summary>
	Task HandleNotificationAsync(string method, JsonElement parameters, CancellationToken ct);
}
