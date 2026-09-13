namespace Weavie.Core.Mcp;

/// <summary>A bundled text-only MCP prompt, also invocable from Weavie's native composer.</summary>
public sealed record McpPrompt {
	/// <summary>The prompt id, used in the slash command and <c>prompts/get</c>.</summary>
	public required string Name { get; init; }

	/// <summary>One-line human-facing description (shown in the slash-command list).</summary>
	public required string Description { get; init; }

	/// <summary>The instruction text injected as a single user message.</summary>
	public required string Text { get; init; }
}

/// <summary>Weavie's bundled user-invoked workflows, shared by MCP and the native composer.</summary>
public static class McpPromptCatalog {
	/// <summary>The prompt definitions exposed to every Weavie session.</summary>
	public static IReadOnlyList<McpPrompt> All { get; } = [
		IssueReportingPrompts.ReportBug,
		IssueReportingPrompts.RequestFeature,
		WorkspaceSetupPrompt.Prompt,
	];

	/// <summary>Resolves an exact catalog identity without accepting client-supplied instructions.</summary>
	public static McpPrompt Require(string name) => All.FirstOrDefault(prompt => prompt.Name == name)
		?? throw new InvalidOperationException($"Unknown Weavie MCP prompt '{name}'.");
}
