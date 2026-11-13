namespace Weavie.Core.Agents;

/// <summary>
/// A dashboard is one journaled row in its conversation; each member block's row names it through
/// <see cref="AgentPaneMessage.ParentItemId"/>. See docs/specs/card-tabs/design.md.
/// </summary>
public static class AgentDashboards {
	/// <summary>The item type of a dashboard row.</summary>
	public const string ItemType = "dashboard";

	/// <summary>The row type that records a dashboard card collapsed or expanded.</summary>
	public const string Collapsed = "dashboard-collapsed";

	private const string ItemPrefix = "dashboard:";

	/// <summary>The transcript item id of dashboard <paramref name="dashboardId"/>.</summary>
	public static string ItemId(string dashboardId) => ItemPrefix + dashboardId;

	/// <summary>Whether <paramref name="row"/> is the row of dashboard <paramref name="dashboardId"/>.</summary>
	public static bool IsMarker(AgentPaneMessage row, string dashboardId) {
		ArgumentNullException.ThrowIfNull(row);
		return row is { Type: "item-completed", ItemType: ItemType } && row.ItemId == ItemId(dashboardId);
	}

	/// <summary>
	/// The rows a block shown into <paramref name="dashboard"/> commits: the dashboard's row when <paramref name="marker"/>
	/// is absent, then the block's row pointing at it. A block added in a later turn carries the line its turn shows.
	/// </summary>
	public static IReadOnlyList<AgentPaneMessage> Rows(AgentPaneMessage? marker, AgentDashboardRequest dashboard, AgentPaneMessage row) {
		ArgumentNullException.ThrowIfNull(dashboard);
		ArgumentNullException.ThrowIfNull(row);
		string itemId = ItemId(dashboard.Id);
		if (marker is null) {
			return [
				new() {
					Type = "item-completed",
					ProviderId = row.ProviderId,
					ThreadId = row.ThreadId,
					TurnId = row.TurnId,
					ItemId = itemId,
					ItemType = ItemType,
					Summary = dashboard.Title,
					Status = "completed",
				},
				row with { ParentItemId = itemId },
			];
		}
		return [row with { ParentItemId = itemId, Text = marker.TurnId == row.TurnId ? null : $"added to {marker.Summary}" }];
	}

	/// <summary>The rows a block shown into no dashboard commits.</summary>
	public static IReadOnlyList<AgentPaneMessage> Loose(AgentPaneMessage row) => [row];

	/// <summary>The editor tab path of dashboard <paramref name="dashboardId"/> in conversation <paramref name="conversationId"/>.</summary>
	public static string TabPath(string? conversationId, string dashboardId) => $"{ItemPrefix}{dashboardId}@{conversationId}";
}

/// <summary>A card in a conversation: a side conversation by its id, or a dashboard within its conversation.</summary>
public sealed record AgentCardRef(string? ConversationId, string? DashboardId);
