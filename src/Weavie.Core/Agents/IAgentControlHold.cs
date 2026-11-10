namespace Weavie.Core.Agents;

/// <summary>Control values the host holds over the user's defaults whenever the primary conversation opens.</summary>
public interface IAgentControlHold {
	/// <summary>The held values by axis id; empty when nothing is held.</summary>
	IReadOnlyDictionary<string, string> Values();
}

/// <summary>Control holds for agents that carry none.</summary>
public static class AgentControlHold {
	/// <summary>A hold that never holds anything.</summary>
	public static IAgentControlHold None { get; } = new Empty();

	private sealed class Empty : IAgentControlHold {
		public IReadOnlyDictionary<string, string> Values() => new Dictionary<string, string>();
	}
}
