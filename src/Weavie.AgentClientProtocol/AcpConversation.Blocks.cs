using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

// A block tool's result carries a token Weavie wrote; the conversation that sees it on its own tool row turns that
// row into the block (show, first pin) or removes it (update, re-pin). See docs/specs/acp-rich-content/design.md.
internal sealed partial class AcpConversation {
	private void BindBlocks() {
		if (_ownsBinding) _binding.Tools.Blocks.Bind(_port.Block, _port.Dashboard);
	}

	private void ClaimBlock(AcpToolState tool) {
		if (_binding.Tools.Blocks.Take(tool.Text, tool.Content) is not { } operation) return;
		if (!_ownsBinding) {
			Notice($"Blocks from subagents are not shown, so {Noun(operation)} '{operation.Id}' was not {Verb(operation)}.");
			return;
		}
		tool.Claimed = true;
		tool.Status = "completed";
		tool.Input = null;
		tool.Text = null;
		tool.Content = null;
		var retraction = Retraction(tool.TurnId, ToolItemId(tool.Id));
		var created = operation switch {
			AgentBlockOperation.Show show => show.Block,
			AgentBlockOperation.Pin pin when _port.Block(pin.Id) is null => pin.Block,
			_ => null,
		};
		var row = created is null
			? retraction
			: ToolMessage(tool) with {
				Type = "item-completed",
				ItemType = "block",
				Category = created.Kind,
				Summary = created.Title,
				BlockId = created.Id,
				Text = null,
				Content = null,
			};
		var rows = operation is AgentBlockOperation.Show { Dashboard: { } dashboard }
			? AgentDashboards.Rows(_port.Dashboard(dashboard.Id), dashboard, row)
			: AgentDashboards.Loose(row);
		if (_port.CommitBlock(Continuation, operation, rows)) {
			foreach (var written in rows) TrackTurnItem(written);
			return;
		}
		Emit(retraction);
		Notice(operation switch {
			AgentBlockOperation.Show => $"Block '{operation.Id}' was already shown, so this call changed nothing.",
			AgentBlockOperation.Pin => $"'{operation.Id}' became a chart or table before this pin landed, so the finding was not pinned.",
			_ => $"Block '{operation.Id}' changed or was removed before this update landed, so it was not applied.",
		});
	}

	// An agent that never reports a block call's result on its row can't show blocks; say so instead of staying silent.
	private void NoticeUnclaimedBlocks() {
		if (!_ownsBinding) return;
		foreach (var operation in _binding.Tools.Blocks.Drain()) {
			string tool = operation switch {
				AgentBlockOperation.Show => "showBlock",
				AgentBlockOperation.Pin => AgentBlockRules.PinTool,
				_ => AgentBlockRules.UpdateTool,
			};
			Notice($"{Definition.Name} did not report the result of {tool}, so {Noun(operation)} '{operation.Id}' was not {Verb(operation)}.");
		}
	}

	private static string Noun(AgentBlockOperation operation) => operation is AgentBlockOperation.Pin ? "finding" : "block";

	private static string Verb(AgentBlockOperation operation) => operation switch {
		AgentBlockOperation.Show => "shown",
		AgentBlockOperation.Pin => "pinned",
		_ => "updated",
	};
}
