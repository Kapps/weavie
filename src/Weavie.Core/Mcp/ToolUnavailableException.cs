namespace Weavie.Core.Mcp;

/// <summary>
/// Thrown by a tool handler when a registry it needs (settings/layout/commands/theming) wasn't wired into this
/// server. Caught at the dispatch boundary and surfaced to the caller as a tool error.
/// </summary>
public sealed class ToolUnavailableException(string message) : Exception(message);
