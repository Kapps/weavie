namespace Weavie.Core.Agents;

/// <summary>Collision-free identity for a pane item within its provider conversation and turn.</summary>
public static class AgentPaneIdentity {
	/// <summary>Returns the identity of an item-bearing message.</summary>
	public static string? ItemKey(AgentPaneMessage message) =>
		ItemKey(message.ThreadId, message.TurnId, message.ItemId);

	/// <summary>Returns an identity from the exact conversation, turn, and item ids.</summary>
	public static string? ItemKey(string? threadId, string? turnId, string? itemId) =>
		string.IsNullOrEmpty(itemId)
			? null
			: $"{KeyPart(threadId)}{KeyPart(turnId)}{KeyPart(itemId)}";

	private static string KeyPart(string? value) => value is null ? "-1:" : $"{value.Length}:{value}";
}
