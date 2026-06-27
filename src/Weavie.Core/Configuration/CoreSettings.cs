namespace Weavie.Core.Configuration;

/// <summary>
/// Registers Weavie's built-in settings and owns the per-platform default resolution for workspace / shell /
/// claude discovery, so every host shares one path through the registry.
/// </summary>
public static class CoreSettings {
	/// <summary>Builds a registry pre-loaded with the built-in settings (workspace, shell, claude path, worktree commands, fonts, editor, theme, diagnostics).</summary>
	public static SettingsRegistry CreateRegistry() {
		var registry = new SettingsRegistry();
		Register(registry);
		return registry;
	}

	/// <summary>Creates a store backed by the core registry over <paramref name="filePath"/> (default <c>~/.weavie/settings.toml</c>).</summary>
	public static SettingsStore CreateStore(string? filePath, bool enableWatcher) =>
		new(CreateRegistry(), filePath, enableWatcher);

	/// <summary>Registers the built-in settings (workspace, shell, claude path, worktree commands, fonts, editor, theme, diagnostics) into <paramref name="registry"/>.</summary>
	public static void Register(SettingsRegistry registry) {
		ArgumentNullException.ThrowIfNull(registry);

		registry.Register(new SettingDefinition {
			Key = "workspace",
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
			Key = "terminal.shell",
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
			Key = "terminal.persistScrollbackKb",
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
			Key = "claude.path",
			Kind = SettingKind.Path,
			Description = "Path to the claude binary (auto-detected when unset).",
			Aliases = ["claude", "claude binary", "claude path"],
			Apply = ApplyMode.NextSession,
			ComputeDefault = DefaultClaudePath,
		});

		registry.Register(new SettingDefinition {
			Key = "claude.resumeSession",
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
			Key = "claude.allowAllTools",
			Kind = SettingKind.Bool,
			Description = "Auto-allow Claude's non-edit tool calls (Bash and other commands) without prompting. "
				+ "This is Weavie's tool-permission axis, separate from how EDITS are handled — edits follow "
				+ "Claude's own mode (cycled with Shift+Tab in the Claude pane), which Weavie observes but does not "
				+ "set. 'Bypass everything' = Claude in acceptEdits + this on. Your own deny rules still win. Takes "
				+ "effect on the next tool call.",
			Aliases = ["allow all tools", "auto allow tools", "auto approve tools", "stop asking", "yolo mode",
				"bypass permissions", "skip permissions", "auto run commands", "allow all"],
			Apply = ApplyMode.Live,
			Default = false,
		});

		registry.Register(new SettingDefinition {
			Key = "worktree.setupCommand",
			Kind = SettingKind.String,
			Description = "Shell command run once in a new session's worktree right after it is created "
				+ "(e.g. 'pnpm install' or 'npm ci'). Empty by default, so nothing runs. It executes via the "
				+ "platform shell with the worktree as the working directory; its output is logged and a "
				+ "non-zero exit is surfaced as a toast — it never blocks or rolls back the new session.",
			Aliases = ["worktree setup", "post-create command", "install deps on new session",
				"bootstrap worktree", "provision worktree", "worktree install command"],
			Apply = ApplyMode.NextSession,
			Default = "",
		});

		registry.Register(new SettingDefinition {
			Key = "worktree.teardownCommand",
			Kind = SettingKind.String,
			Description = "Shell command run once in a worktree right before it is discarded ('git worktree "
				+ "remove'). Empty by default, so nothing runs. It executes via the platform shell with the "
				+ "worktree as the working directory; its output is logged and a non-zero exit is surfaced as "
				+ "a toast, but removal proceeds regardless.",
			Aliases = ["worktree teardown", "pre-remove command", "cleanup on discard",
				"worktree cleanup command", "deprovision worktree"],
			Apply = ApplyMode.NextSession,
			Default = "",
		});

		FontSettings.Register(registry);
		EditorSettings.Register(registry);
		ThemeSettings.Register(registry);

		registry.Register(new SettingDefinition {
			Key = "diagnostics.startupTiming",
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
