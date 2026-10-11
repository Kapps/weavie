namespace Weavie.Core.Agents;

/// <summary>Subagents and background tasks a structured agent runs beside its turns.</summary>
public interface IStructuredAgentBackgroundWork {
	/// <summary>Every live item, plus finished ones until the primary conversation's next prompt.</summary>
	IReadOnlyList<AgentBackgroundItem> BackgroundWork { get; }

	/// <summary>Raised with the complete item list whenever it changes.</summary>
	event Action<IReadOnlyList<AgentBackgroundItem>> BackgroundWorkChanged;

	/// <summary>Asks the agent to stop one stoppable item; false when the agent declined.</summary>
	Task<bool> StopBackgroundTaskAsync(string id);
}
