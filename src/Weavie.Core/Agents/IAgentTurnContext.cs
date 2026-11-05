namespace Weavie.Core.Agents;

/// <summary>Context the host attaches to every agent turn, e.g. the live workflow stage.</summary>
public interface IAgentTurnContext {
	/// <summary>The text to attach to the next turn; empty when there is nothing to say.</summary>
	string Compose();
}

/// <summary>Turn contexts for agents that carry none.</summary>
public static class AgentTurnContext {
	/// <summary>A context that never attaches anything.</summary>
	public static IAgentTurnContext None { get; } = new Empty();

	private sealed class Empty : IAgentTurnContext {
		public string Compose() => "";
	}
}
