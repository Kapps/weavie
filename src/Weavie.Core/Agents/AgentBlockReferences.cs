namespace Weavie.Core.Agents;

/// <summary>The blocks a transcript still shows: a block lives while its reference row does.</summary>
public static class AgentBlockReferences {
	/// <summary>The blocks the materialized rows of <paramref name="ledger"/> show.</summary>
	public static HashSet<(string ProviderId, string? ConversationId, string BlockId)> Of(AgentPaneLedger ledger) {
		ArgumentNullException.ThrowIfNull(ledger);
		return [.. Enumerable.Range(0, ledger.Count).Select(index => ledger.EntryAt(index).Message)
			.Where(IsBlockRow).Select(row => (row.ProviderId, row.ConversationId, row.BlockId!))];
	}

	/// <summary>Whether <paramref name="row"/> is a shown block's reference row.</summary>
	public static bool IsBlockRow(AgentPaneMessage row) =>
		row is { Type: "item-completed", ItemType: "block", BlockId: not null };

	/// <summary>The key that names <paramref name="state"/> among a session's blocks.</summary>
	public static (string ProviderId, string? ConversationId, string BlockId) Key(AgentBlockState state) {
		ArgumentNullException.ThrowIfNull(state);
		return (state.ProviderId, state.ConversationId, state.Block.Id);
	}
}
