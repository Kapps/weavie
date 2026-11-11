using Weavie.Core.Mcp;
using Weavie.Workflows;

namespace Weavie.Core.Agents;

/// <summary>Creates distinct conversation bindings over the loaded session's shared services.</summary>
public sealed class AgentConversationBindings(
	CapabilityRegistryHost registry, IAgentTurnContext mainContext, IAgentControlHold mainControls, Func<WorkflowStageCall?> stageCall) {
	/// <summary>The main context also delivered through terminal providers' input adapter.</summary>
	public IAgentTurnContext MainTurnContext => mainContext;

	/// <summary>Creates a new main incarnation's binding.</summary>
	public AgentConversationBinding Main() => new() {
		TurnContext = mainContext,
		ControlHold = mainControls,
		Tools = registry.CreateGrant(stageCall, conversation: null),
	};

	/// <summary>Creates a <c>/btw</c> side conversation's binding, without workflow completion authority.</summary>
	public AgentConversationBinding Side() => new() {
		TurnContext = AgentTurnContext.None,
		ControlHold = AgentControlHold.None,
		Tools = registry.CreateGrant(() => null, "BTW"),
	};
}
