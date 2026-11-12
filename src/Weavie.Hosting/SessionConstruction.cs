using Weavie.Core;
using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Corrections;
using Weavie.Core.Inference;
using Weavie.Core.Layout;
using Weavie.Core.Mcp;
using Weavie.Core.Sessions;
using Weavie.Core.Theming;
using Weavie.Core.Workflows;
using Weavie.Core.Workspaces;
using Weavie.Workflows;

namespace Weavie.Hosting;

/// <summary>The services every session of one workspace shares.</summary>
internal sealed record SessionServices {
	public required SettingsStore Settings { get; init; }

	public required LayoutStore Layout { get; init; }

	public required CommandRegistry CommandRegistry { get; init; }

	public required KeybindingStore Keybindings { get; init; }

	public required ThemeOverridesStore ThemeOverrides { get; init; }

	/// <summary>The workspace's shared correction ring each session records into.</summary>
	public required CorrectionCorpus Corrections { get; init; }

	public required IInferenceService Inference { get; init; }

	public required AgentConsultation AgentConsultation { get; init; }

	public required IPtyLauncher PtyLauncher { get; init; }

	public required HostRuntimeInfo Runtime { get; init; }

	public required SessionWorkflowServices Workflow { get; init; }
}

/// <summary>What a session's workflow runner and registry are built from.</summary>
internal sealed record SessionWorkflowServices {
	public required WorkflowRoleStore Roles { get; init; }

	/// <summary>A new run's documents directory, from its id.</summary>
	public required Func<string, string> DocumentsDirectory { get; init; }

	/// <summary>The decider for a session rooted at the given worktree.</summary>
	public required Func<string, IWorkflowDecider> Decider { get; init; }

	/// <summary>Fills a definition's roles for a session on the given provider.</summary>
	public required Func<AgentProviderInfo, IWorkflowRoleResolver> RoleResolver { get; init; }
}

/// <summary>Where one session lives on disk; every per-session path is keyed by its worktree.</summary>
internal sealed record SessionPaths {
	/// <summary>The session's own root: the worktree its agent and terminals open in.</summary>
	public required string WorkspaceRoot { get; init; }

	/// <summary>The workspace whose overlay holds the workspace-scoped settings the agent changes.</summary>
	public required string SettingsWorkspaceRoot { get; init; }

	/// <summary>Untitled buffers, outside the worktree so they never reach the tree, index, git, or agent.</summary>
	public required string ScratchDir { get; init; }

	/// <summary>Images pasted into the agent, so unloading one session's never touches another's.</summary>
	public required string PastedImagesDir { get; init; }

	/// <summary>Scrollback of the visible terminal an ACP agent signs in through.</summary>
	public required string AuthenticationTerminalLog { get; init; }

	/// <summary>A shell terminal's scrollback log, from its id.</summary>
	public required Func<string, string> ShellScrollback { get; init; }

	/// <summary>The paths of the session rooted at <paramref name="cwd"/> in <paramref name="workspace"/>.</summary>
	public static SessionPaths For(WorkspaceId workspace, string workspaceRoot, string cwd) {
		string worktree = WorkspaceId.ForPath(cwd).Value;
		return new() {
			WorkspaceRoot = cwd,
			SettingsWorkspaceRoot = workspaceRoot,
			ScratchDir = Path.Combine(WeaviePaths.WorkspaceScratchDir(workspace), worktree),
			PastedImagesDir = Path.Combine(WeaviePaths.WorkspacePastedImagesDir(workspace), worktree),
			AuthenticationTerminalLog = WeaviePaths.WorkspaceTerminalLogFile(workspace, worktree, "agent-authentication"),
			ShellScrollback = terminalId => WeaviePaths.WorkspaceTerminalLogFile(workspace, worktree, $"shell-{terminalId}"),
		};
	}
}

/// <summary>What a session asks of the host that owns it.</summary>
internal sealed record SessionOwner {
	/// <summary>The session's view of the other sessions on the host.</summary>
	public required ISessionPeers Peers { get; init; }

	/// <summary>Whether terminal input is frozen while the host drains for an update.</summary>
	public required Func<bool> InputFrozen { get; init; }

	/// <summary>Runs the accept action if the host admits terminal input; the flag says the user initiated it.</summary>
	public required Action<bool, Action> AcceptTerminalInput { get; init; }

	/// <summary>Records a shell terminal's new size (columns, rows).</summary>
	public required Action<int, int> ShellResized { get; init; }
}
