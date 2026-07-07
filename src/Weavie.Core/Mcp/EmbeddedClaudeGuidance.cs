namespace Weavie.Core.Mcp;

/// <summary>
/// Compatibility wrapper for Claude's existing system-prompt appendix file.
/// </summary>
public static class EmbeddedClaudeGuidance {
	/// <summary>The appendix text. Plain UTF-8; written to a file and referenced by path.</summary>
	public const string SystemPromptAppendix = EmbeddedAgentGuidance.Instructions;

	/// <summary>The static appendix plus a host-runtime block describing what <paramref name="runtime"/> is running.</summary>
	public static string Compose(HostRuntimeInfo runtime) => EmbeddedAgentGuidance.Compose(runtime);
}
