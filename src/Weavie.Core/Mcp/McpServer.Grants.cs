using Weavie.Workflows;

namespace Weavie.Core.Mcp;

public sealed partial class McpServer {
	private readonly Lock _grantsGate = new();
	private readonly HashSet<CapabilityRegistryGrant> _grants = [];

	/// <summary>Registers a distinct credential whose stage tools are captured at call admission.</summary>
	public CapabilityRegistryGrant CreateGrant(Func<WorkflowStageCall?> stageCall) => AddGrant(AgentSessionCredential.Create(), stageCall);

	private CapabilityRegistryGrant AddGrant(AgentSessionCredential credential, Func<WorkflowStageCall?> stageCall) {
		lock (_grantsGate) {
			ObjectDisposedException.ThrowIf(_disposed != 0, this);
			var grant = new CapabilityRegistryGrant(this, credential, stageCall);
			_grants.Add(grant);
			return grant;
		}
	}

	internal void RemoveGrant(CapabilityRegistryGrant grant) {
		lock (_grantsGate) _grants.Remove(grant);
	}
}
