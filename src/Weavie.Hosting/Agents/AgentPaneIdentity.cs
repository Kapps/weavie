using Weavie.Core.Agents;

namespace Weavie.Hosting.Agents;

internal static class AgentPaneIdentity {
	public static string? ItemKey(AgentPaneMessage message) =>
		ItemKey(message.ThreadId, message.TurnId, message.ItemId);

	public static string? ItemKey(string? threadId, string? turnId, string? itemId) =>
		string.IsNullOrEmpty(itemId)
			? null
			: $"{KeyPart(threadId)}{KeyPart(turnId)}{KeyPart(itemId)}";

	private static string KeyPart(string? value) => value is null ? "-1:" : $"{value.Length}:{value}";
}
