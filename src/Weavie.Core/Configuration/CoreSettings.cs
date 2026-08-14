using Weavie.Core.Workspaces;

namespace Weavie.Core.Configuration;

/// <summary>
/// Registers Weavie's built-in settings and owns the per-platform default resolution for workspace / shell /
/// claude discovery, so every host shares one path through the registry.
/// </summary>
public static class CoreSettings {
	/// <summary>The workspace path setting.</summary>
	public const string Workspace = "workspace";

	/// <summary>The plain terminal shell setting.</summary>
	public const string TerminalShell = "terminal.shell";

	/// <summary>The persisted terminal scrollback size setting.</summary>
	public const string TerminalPersistScrollbackKb = "terminal.persistScrollbackKb";

	/// <summary>The terminal output coalescing interval setting.</summary>
	public const string TerminalOutputCoalesceMs = "terminal.outputCoalesceMs";

	/// <summary>The Claude executable path setting.</summary>
	public const string ClaudePath = "claude.path";

	/// <summary>The Claude conversation resume setting.</summary>
	public const string ClaudeResumeSession = "claude.resumeSession";

	/// <summary>The pull-request review prompt setting.</summary>
	public const string PullRequestAutoReviewPrompt = "pr.autoReviewPrompt";

	/// <summary>The worktree setup command setting.</summary>
	public const string WorktreeSetupCommand = "worktree.setupCommand";

	/// <summary>The worktree teardown command setting.</summary>
	public const string WorktreeTeardownCommand = "worktree.teardownCommand";

	/// <summary>The startup timing diagnostics setting.</summary>
	public const string DiagnosticsStartupTiming = "diagnostics.startupTiming";

	/// <summary>Builds a registry pre-loaded with the built-in settings (workspace, shell, claude path, worktree commands, fonts, editor, theme, diagnostics).</summary>
	public static SettingsRegistry CreateRegistry() {
		var registry = new SettingsRegistry();
		Register(registry);
		return registry;
	}

	/// <summary>Creates a store backed by the core registry over <paramref name="filePath"/> (default <c>~/.weavie/settings.toml</c>).</summary>
	public static SettingsStore CreateStore(string? filePath, bool enableWatcher) =>
		new(CreateRegistry(), filePath, enableWatcher, root => WeaviePaths.WorkspaceSettingsFile(WorkspaceId.ForPath(root)));

	/// <summary>Registers the built-in settings (workspace, shell, claude path, worktree commands, fonts, editor, theme, diagnostics) into <paramref name="registry"/>.</summary>
	public static void Register(SettingsRegistry registry) {
		ArgumentNullException.ThrowIfNull(registry);

		registry.Register(new SettingDefinition {
			Key = Workspace,
			Kind = SettingKind.Path,
			Description = "Directory Claude and the terminal open in (the IDE workspace).",
			Aliases = ["workspace", "working directory", "project folder"],
			Apply = ApplyMode.RestartRequired,
			// No default: an unset workspace is no workspace (the launch empty state → welcome screen), never a
			// silent fall-back to the home directory.
			Validate = static value => value is string dir && Directory.Exists(dir)
				? ValidationResult.Success
				: ValidationResult.Failure($"workspace '{value}' is not an existing directory."),
		});

		registry.Register(new SettingDefinition {
			Key = TerminalShell,
			Kind = SettingKind.String,
			Description = "Shell for the plain terminal pane.",
			Aliases = ["shell", "my shell", "terminal shell"],
			Apply = ApplyMode.ReopensTerminal,
			ComputeDefault = DefaultShell,
			Validate = static value => value is string shell
				&& (ExecutableFinder.FindOnPath(shell) is not null || File.Exists(shell))
				? ValidationResult.Success
				: ValidationResult.Failure($"shell '{value}' was not found on PATH."),
		});

		registry.Register(new SettingDefinition {
			Key = TerminalPersistScrollbackKb,
			Kind = SettingKind.Int,
			Description = "How much of the shell terminal's recent output (in KiB) to persist on disk per "
				+ "session, so a reattaching client (a browser refresh, a session switch, a resumed remote "
				+ "backend) replays a coherent screen instead of a blank pane — and a restarted shell shows "
				+ "its previous output faded. 256 by default; 0 disables persistence. Claude is never logged "
				+ "(it resumes its own conversation). Takes effect on the next session.",
			Aliases = ["scrollback", "terminal history", "persist scrollback", "shell history size",
				"terminal scrollback", "remember terminal output"],
			Apply = ApplyMode.NextSession,
			Default = 256L,
			Validate = static value => value is long kb && kb >= 0
				? ValidationResult.Success
				: ValidationResult.Failure("terminal.persistScrollbackKb must be 0 (off) or a positive number of KiB."),
		});

		registry.Register(new SettingDefinition {
			Key = TerminalOutputCoalesceMs,
			Kind = SettingKind.Int,
			Description = "How long (milliseconds) to batch a terminal pane's live output into one update before "
				+ "sending it to the page. Batching keeps a burst of output (a build, a big file, `seq`) from "
				+ "flooding the bridge and freezing the UI. 16 by default — one frame at 60fps, imperceptible; "
				+ "0 sends every chunk immediately (no batching). Takes effect on the next session.",
			Aliases = ["terminal batching", "coalesce terminal output", "terminal output batching",
				"output flush interval"],
			Apply = ApplyMode.NextSession,
			Default = 16L,
			Validate = static value => value is long ms && ms >= 0
				? ValidationResult.Success
				: ValidationResult.Failure("terminal.outputCoalesceMs must be 0 (off) or a positive number of milliseconds."),
		});

		registry.Register(new SettingDefinition {
			Key = AgentSettings.PaneCoalesceMs,
			Kind = SettingKind.Int,
			Description = "How long (milliseconds) to batch a native agent pane's live messages into one update "
				+ "before sending it to the page. Batching keeps a fast turn (or a resumed thread replaying its "
				+ "whole history) from flooding the bridge and dropping a network-slow page mid-stream. 16 by "
				+ "default — one frame at 60fps, imperceptible; 0 sends every message immediately (no batching). "
				+ "Takes effect on the next session.",
			Aliases = ["agent pane batching", "coalesce agent output", "agent output batching",
				"pane flush interval"],
			Apply = ApplyMode.NextSession,
			Default = 16L,
			Validate = static value => value is long ms && ms >= 0
				? ValidationResult.Success
				: ValidationResult.Failure("agent.paneCoalesceMs must be 0 (off) or a positive number of milliseconds."),
		});

		registry.Register(new SettingDefinition {
			Key = AgentSettings.DefaultProvider,
			Kind = SettingKind.String,
			Description = "Agent provider used for newly-created sessions. Existing sessions keep their provider.",
			Aliases = ["agent provider", "default agent", "new session agent", "provider"],
			Apply = ApplyMode.NextSession,
			Default = "claude",
		});

		registry.Register(new SettingDefinition {
			Key = AgentSettings.AllowAllPermissions,
			Kind = SettingKind.Bool,
			Description = "Automatically select the strongest allow option advertised by an ACP agent. "
				+ "On by default and applied to the next permission request.",
			Aliases = ["allow all tools", "auto approve tools", "yolo mode", "bypass permissions"],
			Apply = ApplyMode.Live,
			Default = true,
		});

		registry.Register(new SettingDefinition {
			Key = AgentSettings.MiddleClickAutoscroll,
			Kind = SettingKind.Bool,
			Description = "Use middle-click autoscroll in the Linux structured-agent transcript. On by default.",
			Aliases = ["middle click autoscroll", "middle mouse scrolling", "Linux autoscroll"],
			Apply = ApplyMode.Live,
			Default = true,
		});

		registry.Register(new SettingDefinition {
			Key = InferenceSettings.Enabled,
			Kind = SettingKind.Bool,
			Description = "Allow Weavie features to make isolated model queries through the selected provider's optional "
				+ "inference capability. Calls never enter the interactive session transcript. "
				+ "Off by default. Takes effect on the next query.",
			Aliases = ["ad hoc inference", "utility inference", "model queries", "ai suggestions"],
			Apply = ApplyMode.Live,
			Default = false,
		});

		registry.Register(new SettingDefinition {
			Key = InferenceSettings.AllowAutomatic,
			Kind = SettingKind.Bool,
			Description = "Allow Weavie to spend inference tokens without a directly-triggering user action, such as "
					+ "branch-name preview or continuous review after an edit. Explicit actions such as reviewing a plan or "
					+ "diagnosing a test failure do not require this. Off by default. Takes effect on the next query.",
			Aliases = ["automatic inference", "background ai suggestions", "continuous ai review"],
			Apply = ApplyMode.Live,
			Default = false,
		});

		registry.Register(new SettingDefinition {
			Key = ClaudePath,
			Kind = SettingKind.Path,
			Description = "Path to the claude binary (auto-detected when unset).",
			Aliases = ["claude", "claude binary", "claude path"],
			Apply = ApplyMode.NextSession,
			ComputeDefault = DefaultClaudePath,
		});

		registry.Register(new SettingDefinition {
			Key = ClaudeResumeSession,
			Kind = SettingKind.Bool,
			Description = "Resume the previous Claude conversation when a session reopens, instead of cold-starting "
				+ "a fresh one. Weavie assigns each session's working directory a stable Claude session id and "
				+ "reattaches to it (claude --resume) on the next launch. On by default. Takes effect on the next "
				+ "session launch.",
			Aliases = ["resume claude", "resume session", "continue claude", "remember conversation",
				"persist claude session", "auto resume"],
			Apply = ApplyMode.NextSession,
			Default = true,
		});

		registry.Register(new SettingDefinition {
			Key = PullRequestAutoReviewPrompt,
			Kind = SettingKind.Bool,
			Description = "When you open a pull request as a session, automatically seed Claude's first message "
				+ "asking it to look at the branch's changes and help address review feedback. On by default; "
				+ "turn it off to open the PR (and its diff navigator) without prompting Claude. Takes effect the "
				+ "next time a PR is opened.",
			Aliases = ["pr review prompt", "auto review prompt", "auto prompt claude on pr", "seed pr prompt",
				"prompt claude to review", "ask claude to review pr"],
			Apply = ApplyMode.Live,
			Default = true,
		});

		registry.Register(new SettingDefinition {
			Key = WorktreeSetupCommand,
			Kind = SettingKind.String,
			Description = "Shell command run once in a new session's worktree right after it is created "
				+ "(e.g. 'pnpm install' or 'npm ci'). Empty by default, so nothing runs. It executes via the "
				+ "platform shell with the worktree as the working directory; its output is logged and a "
				+ "non-zero exit is surfaced as a toast — it never blocks or rolls back the new session.",
			Aliases = ["worktree setup", "post-create command", "install deps on new session",
				"bootstrap worktree", "provision worktree", "worktree install command"],
			Apply = ApplyMode.NextSession,
			// Per-workspace: stored out-of-repo in ~/.weavie/workspaces/<id>/settings.toml (reads fall back to the user file).
			Scope = SettingScope.Workspace,
			Default = "",
		});

		registry.Register(new SettingDefinition {
			Key = WorktreeTeardownCommand,
			Kind = SettingKind.String,
			Description = "Shell command run once in a worktree right before it is discarded ('git worktree "
				+ "remove'). Empty by default, so nothing runs. It executes via the platform shell with the "
				+ "worktree as the working directory; its output is logged and a non-zero exit is surfaced as "
				+ "a toast, but removal proceeds regardless.",
			Aliases = ["worktree teardown", "pre-remove command", "cleanup on discard",
				"worktree cleanup command", "deprovision worktree"],
			// Per-workspace, like its setupCommand sibling: a teardown command belongs to one repo, not every workspace.
			Scope = SettingScope.Workspace,
			Apply = ApplyMode.NextSession,
			Default = "",
		});

		FontSettings.Register(registry);
		EditorSettings.Register(registry);
		ThemeSettings.Register(registry);
		TestSettings.Register(registry);
		NotificationSettings.Register(registry);
		CorrectionsSettings.Register(registry);
		MessageSettings.Register(registry);

		registry.Register(new SettingDefinition {
			Key = DiagnosticsStartupTiming,
			Kind = SettingKind.Bool,
			Description = "Log startup phase timings (window→navigate on the host, navigate→shell→editor "
				+ "in the web app) to the console. Off by default; for diagnosing launch latency.",
			Aliases = ["startup timing", "launch timing", "boot timing", "startup profiling"],
			// Captured during launch, so a change takes effect on the next start.
			Apply = ApplyMode.RestartRequired,
			Default = false,
		});
	}

	/// <summary>
	/// The system-suggested shell: Windows prefers PowerShell 7 (<c>pwsh</c>) then Windows PowerShell; Unix
	/// uses <c>$SHELL</c> then <c>/bin/zsh</c>. The lowest-precedence layer.
	/// </summary>
	private static object? DefaultShell() {
		if (OperatingSystem.IsWindows()) {
			return ExecutableFinder.FindOnPath("pwsh") is not null ? "pwsh" : "powershell";
		}

		string? shell = Environment.GetEnvironmentVariable("SHELL");
		return !string.IsNullOrEmpty(shell) && File.Exists(shell) ? shell : "/bin/zsh";
	}

	/// <summary>
	/// The auto-detected claude binary: <c>claude</c> on PATH, else the native-installer location, else bare
	/// <c>claude</c> (let the launcher search PATH).
	/// </summary>
	private static object? DefaultClaudePath() {
		string? onPath = ExecutableFinder.FindOnPath("claude");
		if (onPath is not null) {
			return onPath;
		}

		if (OperatingSystem.IsWindows()) {
			string local = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
			return File.Exists(local) ? local : "claude";
		}

		return "claude";
	}

}
