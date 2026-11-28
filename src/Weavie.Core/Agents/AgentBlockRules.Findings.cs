namespace Weavie.Core.Agents;

// Findings: a block whose title states one thing the agent learned. See docs/specs/findings-board.md.
public static partial class AgentBlockRules {
	/// <summary>The MCP tool that pins a finding.</summary>
	public const string PinTool = "pinFinding";

	/// <summary>Validates a pin against the conversation's current block with that id; a finding has no length limit.</summary>
	public static AgentBlockOperation Pin(AgentFindingRequest request, AgentBlock? current) {
		ArgumentNullException.ThrowIfNull(request);
		if (!BlockId().IsMatch(request.Id)) Refuse($"id must be {IdRule}.");
		if (current is { Finding: null }) Refuse($"'{request.Id}' is a {current.Kind} you showed; pin the finding under another id.");
		string title = request.Title.Trim();
		if (title.Length == 0) Refuse($"{PinTool} requires a 'title'.");
		string? detail = string.IsNullOrWhiteSpace(request.Detail) ? null : request.Detail.Trim();
		return new AgentBlockOperation.Pin(new AgentBlock { Id = request.Id, Title = title, Finding = new AgentFinding { Detail = detail } });
	}

	private static AgentBlock? Repin(AgentBlock? current, AgentBlock pinned, long nowMs) =>
		current is null ? pinned : current.Finding is null ? null : pinned with { UpdatedAtMs = nowMs };
}
