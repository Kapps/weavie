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
		Title = "Main",
		TurnContext = mainContext,
		ControlHold = mainControls,
		Tools = registry.CreateGrant(stageCall),
	};

	/// <summary>Creates the binding of the <c>/btw</c> side conversation <paramref name="conversationId"/>, which calls the registry as itself.</summary>
	public AgentConversationBinding Side(string conversationId) => new() {
		Title = "Conversation",
		TurnContext = AgentTurnContext.None,
		ControlHold = AgentControlHold.None,
		Tools = registry.CreateSideGrant(conversationId),
	};
}
