namespace Weavie.Core.Agents;

/// <summary>
/// The reply an agent gives when a turn has nothing to tell the user, such as one that only refreshed a block. A
/// message that is exactly this is never shown or saved, so a periodic turn leaves no row behind.
/// </summary>
public static class AgentNoReply {
	/// <summary>The whole text of a no-reply message.</summary>
	public const string Text = "[[weavie:no-reply]]";

	/// <summary>Whether <paramref name="message"/> is the no-reply message, whatever whitespace surrounds it.</summary>
	public static bool Is(string message) => message.AsSpan().Trim().SequenceEqual(Text);

	/// <summary>Whether a message that reads <paramref name="soFar"/> can still end as the no-reply message.</summary>
	public static bool CouldBecome(string soFar) => Text.AsSpan().StartsWith(soFar.AsSpan().TrimStart()) || Is(soFar);
}
