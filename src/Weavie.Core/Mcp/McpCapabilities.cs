using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.Layout;
using Weavie.Core.Review.Hints;
using Weavie.Core.Sessions;
using Weavie.Core.Theming;
using Weavie.Core.Workflows;

namespace Weavie.Core.Mcp;

/// <summary>What an <see cref="McpServer"/> offers its client; a store that is absent has no tools.</summary>
public sealed record McpCapabilities {
	/// <summary>Whether the IDE RPC tools (open editors, selection, diagnostics) are advertised.</summary>
	public required bool ExposeIdeTools { get; init; }

	/// <summary>The settings store behind the settings tools.</summary>
	public required SettingsStore? Settings { get; init; }

	/// <summary>The workspace whose overlay holds workspace-scoped settings, or empty for none.</summary>
	public required string SettingsWorkspaceRoot { get; init; }

	/// <summary>The layout store behind the layout tools.</summary>
	public required LayoutStore? Layout { get; init; }

	/// <summary>The editor's active file and selection.</summary>
	public required EditorStore? Editor { get; init; }

	/// <summary>The dispatcher behind the command tools.</summary>
	public required CommandDispatcher? Commands { get; init; }

	/// <summary>The effective keybindings listed beside commands.</summary>
	public required KeybindingStore? Keybindings { get; init; }

	/// <summary>The store behind the theme tools.</summary>
	public required ThemeOverridesStore? ThemeOverrides { get; init; }

	/// <summary>The other sessions on the host.</summary>
	public required ISessionPeers Sessions { get; init; }

	/// <summary>The agents that can be consulted.</summary>
	public required AgentConsultation Agents { get; init; }

	/// <summary>Where review hints are reported.</summary>
	public required IReviewHintSink ReviewHints { get; init; }

	/// <summary>The session's workflows and run.</summary>
	public required IWorkflowRegistry Workflows { get; init; }

	/// <summary>The IDE server's capabilities: the IDE tools over <paramref name="editor"/> and nothing else.</summary>
	public static McpCapabilities Ide(EditorStore? editor) => new() {
		ExposeIdeTools = true,
		Settings = null,
		SettingsWorkspaceRoot = "",
		Layout = null,
		Editor = editor,
		Commands = null,
		Keybindings = null,
		ThemeOverrides = null,
		Sessions = NoSessionPeers.Instance,
		Agents = AgentConsultation.None,
		ReviewHints = ReviewHintSink.None,
		Workflows = WorkflowRegistry.None,
	};
}
