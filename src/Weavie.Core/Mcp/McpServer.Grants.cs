using Weavie.Core.Sessions;
using Weavie.Workflows;

namespace Weavie.Core.Mcp;

public sealed partial class McpServer {
	private readonly Lock _grantsGate = new();
	private readonly HashSet<CapabilityRegistryGrant> _grants = [];

	/// <summary>Registers a distinct credential whose stage tools are captured at call admission.</summary>
	public CapabilityRegistryGrant CreateGrant(Func<WorkflowStageCall?> stageCall) => AddGrant(AgentSessionCredential.Create(), stageCall, _sessions);

	/// <summary>Registers a distinct credential that calls as the session's <c>/btw</c> side session <paramref name="conversationId"/>.</summary>
	public CapabilityRegistryGrant CreateSideGrant(string conversationId) =>
		AddGrant(AgentSessionCredential.Create(), () => null, _sessions.Side(conversationId));

	private CapabilityRegistryGrant AddGrant(AgentSessionCredential credential, Func<WorkflowStageCall?> stageCall, ISessionPeers sessions) {
		lock (_grantsGate) {
			ObjectDisposedException.ThrowIf(_disposed != 0, this);
			var grant = new CapabilityRegistryGrant(this, credential, stageCall, sessions);
			_grants.Add(grant);
			return grant;
		}
	}

	internal void RemoveGrant(CapabilityRegistryGrant grant) {
		lock (_grantsGate) _grants.Remove(grant);
	}
}
