namespace Weavie.Core.Agents;

/// <summary>Synchronously folds normalized agent events into Weavie state and returns response enrichment.</summary>
public interface IAgentEventSink {
	/// <summary>Observes <paramref name="value"/> before the provider replies to its event source.</summary>
	AgentEventFeedback Observe(AgentEvent value);
}
