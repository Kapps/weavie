namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	/// <inheritdoc/>
	public string Source(string? conversationId, string blockId, string hash) {
		ArgumentException.ThrowIfNullOrEmpty(blockId);
		ArgumentException.ThrowIfNullOrEmpty(hash);
		return _sessions.ReadBlockSource(_definition.Id, _context.Workspace, conversationId, blockId, hash);
	}
}
