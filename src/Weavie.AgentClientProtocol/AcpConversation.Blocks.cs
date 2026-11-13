using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

// A block tool's result carries a token Weavie wrote; the conversation that sees it on its own tool row turns that
// row into the block (show) or removes it (update). See docs/specs/acp-rich-content/design.md.
internal sealed partial class AcpConversation {
	private void BindBlocks() {
		if (_ownsBinding) _binding.Tools.Blocks.Bind(_port.Block, _port.Dashboard);
	}

	private void ClaimBlock(AcpToolState tool) {
		if (_binding.Tools.Blocks.Take(tool.Text, tool.Content) is not { } operation) return;
		if (!_ownsBinding) {
			Notice($"Blocks from subagents are not shown, so block '{operation.Id}' was not {Verb(operation)}.");
			return;
		}
		tool.Claimed = true;
		tool.Status = "completed";
		tool.Input = null;
		tool.Text = null;
		tool.Content = null;
		var retraction = Retraction(tool.TurnId, ToolItemId(tool.Id));
		var row = operation is AgentBlockOperation.Show show
			? ToolMessage(tool) with {
				Type = "item-completed",
				ItemType = "block",
				Category = show.Block.Kind,
				Summary = show.Block.Title,
				BlockId = show.Block.Id,
				Text = null,
				Content = null,
			}
			: retraction;
		var rows = operation is AgentBlockOperation.Show { Dashboard: { } dashboard }
			? AgentDashboards.Rows(_port.Dashboard(dashboard.Id), dashboard, row)
			: AgentDashboards.Loose(row);
		if (_port.CommitBlock(Continuation, operation, rows)) {
			foreach (var written in rows) TrackTurnItem(written);
			return;
		}
		Emit(retraction);
		Notice(operation is AgentBlockOperation.Show
			? $"Block '{operation.Id}' was already shown, so this call changed nothing."
			: $"Block '{operation.Id}' changed or was removed before this update landed, so it was not applied.");
	}

	// An agent that never reports a block call's result on its row can't show blocks; say so instead of staying silent.
	private void NoticeUnclaimedBlocks() {
		if (!_ownsBinding) return;
		foreach (var operation in _binding.Tools.Blocks.Drain()) {
			Notice($"{Definition.Name} did not report the result of {(operation is AgentBlockOperation.Show ? "showBlock" : "updateBlock")}, "
				+ $"so block '{operation.Id}' was not {Verb(operation)}.");
		}
	}

	private static string Verb(AgentBlockOperation operation) => operation is AgentBlockOperation.Show ? "shown" : "updated";
}
