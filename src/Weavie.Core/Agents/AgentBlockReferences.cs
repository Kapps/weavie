namespace Weavie.Core.Agents;

/// <summary>The blocks a transcript still shows: a block lives while its reference row does.</summary>
public static class AgentBlockReferences {
	/// <summary>The (provider, conversation, block) keys referenced by the materialized <paramref name="rows"/>.</summary>
	public static HashSet<(string ProviderId, string? ConversationId, string BlockId)> Of(IEnumerable<AgentPaneMessage> rows) =>
		[.. rows.Where(IsBlockRow).Select(row => (row.ProviderId, row.ConversationId, row.BlockId!))];

	/// <summary>Whether <paramref name="row"/> is a shown block's reference row.</summary>
	public static bool IsBlockRow(AgentPaneMessage row) =>
		row is { Type: "item-completed", ItemType: "block", BlockId: not null };
}
