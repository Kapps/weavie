using Weavie.Core.Workspaces;

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
		new(CreateRegistry(), filePath, enableWatcher, root => WeaviePaths.WorkspaceSettingsFile(WorkspaceId.ForPath(root)));

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
			Key = "terminal.outputCoalesceMs",
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
			Key = AgentSettings.DefaultProvider,
			Kind = SettingKind.String,
			Description = "Agent provider used for newly-created sessions. Existing sessions keep their provider. "
				+ "Claude is the default; Codex is selectable only once its native parity gate is available.",
			Aliases = ["agent provider", "default agent", "new session agent", "provider", "codex provider"],
			Apply = ApplyMode.NextSession,
			Default = "claude",
			Validate = static value => value is string provider
				&& (string.Equals(provider, "claude", StringComparison.Ordinal)
					|| string.Equals(provider, "codex", StringComparison.Ordinal))
				? ValidationResult.Success
				: ValidationResult.Failure("agent.defaultProvider must be 'claude' or 'codex'."),
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
			Description = "Bypass agent permission prompts. Claude's permission hooks are auto-accepted without "
				+ "changing its edit mode. Codex runs with danger-full-access and never asks for approval. Takes effect "
				+ "on the next tool call or Codex turn.",
			Aliases = ["allow all tools", "auto allow tools", "auto approve tools", "stop asking", "yolo mode",
				"bypass permissions", "skip permissions", "auto run commands", "allow all"],
			Apply = ApplyMode.Live,
			Default = false,
		});

		registry.Register(new SettingDefinition {
			Key = "codex.path",
			Kind = SettingKind.Path,
			Description = "Path to the codex binary used for native Codex app-server sessions. Auto-detected when "
				+ "unset; set this when the Codex found on PATH cannot launch app-server correctly. Takes effect "
				+ "on the next Codex session.",
			Aliases = ["codex", "codex binary", "codex path"],
			Apply = ApplyMode.NextSession,
			ComputeDefault = DefaultCodexPath,
		});

		registry.Register(new SettingDefinition {
			Key = "codex.model",
			Kind = SettingKind.String,
			Description = "Model passed to Codex app-server when starting a native Codex thread. Empty means Codex "
				+ "uses its own configured default. Takes effect on the next Codex session.",
			Aliases = ["codex model", "codex default model"],
			Apply = ApplyMode.NextSession,
			Default = "",
		});

		registry.Register(new SettingDefinition {
			Key = "codex.sandbox",
			Kind = SettingKind.String,
			Description = "Sandbox mode passed to native Codex sessions: read-only, workspace-write, or "
				+ "danger-full-access. Takes effect on the next Codex session.",
			Aliases = ["codex sandbox", "codex permissions sandbox"],
			Apply = ApplyMode.NextSession,
			Default = "workspace-write",
			Validate = static value => value is string mode
				&& (string.Equals(mode, "read-only", StringComparison.Ordinal)
					|| string.Equals(mode, "workspace-write", StringComparison.Ordinal)
					|| string.Equals(mode, "danger-full-access", StringComparison.Ordinal))
				? ValidationResult.Success
				: ValidationResult.Failure("codex.sandbox must be read-only, workspace-write, or danger-full-access."),
		});

		registry.Register(new SettingDefinition {
			Key = "codex.approvalPolicy",
			Kind = SettingKind.String,
			Description = "Approval policy passed to native Codex sessions: untrusted, on-request, or never. "
				+ "Takes effect on the next Codex session.",
			Aliases = ["codex approvals", "codex approval policy", "codex ask approval"],
			Apply = ApplyMode.NextSession,
			Default = "on-request",
			// "on-failure" was removed in Codex 0.143 (the app-server API rejects it); a stale persisted value
			// resolves to the default, matching upstream's own on-failure → on-request config migration.
			Validate = static value => value is string policy
				&& (string.Equals(policy, "untrusted", StringComparison.Ordinal)
					|| string.Equals(policy, "on-request", StringComparison.Ordinal)
					|| string.Equals(policy, "never", StringComparison.Ordinal))
				? ValidationResult.Success
				: ValidationResult.Failure("codex.approvalPolicy must be untrusted, on-request, or never."),
		});

		// No Validate: efforts/tiers are per-model and open-ended (xhigh/max/ultra today, more tomorrow), so a
		// fixed enum would reject future-valid values. A bad value surfaces as a loud Codex error on the next turn.
		registry.Register(new SettingDefinition {
			Key = "codex.effort",
			Kind = SettingKind.String,
			Description = "Reasoning effort passed to native Codex sessions (e.g. low, medium, high, xhigh). Empty "
				+ "means Codex uses the model's default effort. Valid values depend on the model. Takes effect on "
				+ "the next Codex session.",
			Aliases = ["codex effort", "codex reasoning effort", "reasoning effort"],
			Apply = ApplyMode.NextSession,
			Default = "",
		});

		registry.Register(new SettingDefinition {
			Key = "codex.serviceTier",
			Kind = SettingKind.String,
			Description = "Service tier passed to native Codex sessions. Empty (or 'standard') uses the standard "
				+ "tier; 'priority' selects Fast Mode where the model supports it. Takes effect on the next Codex "
				+ "session.",
			Aliases = ["codex service tier", "codex fast mode", "fast mode"],
			Apply = ApplyMode.NextSession,
			Default = "",
		});

		registry.Register(new SettingDefinition {
			Key = "pr.autoReviewPrompt",
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
			Key = "worktree.setupCommand",
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
			Key = "worktree.teardownCommand",
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

	/// <summary>The auto-detected Codex binary; Windows avoids the packaged alias because child processes cannot reliably execute it.</summary>
	private static object? DefaultCodexPath() {
		if (OperatingSystem.IsWindows()) {
			string standalone = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				".codex", "packages", "standalone", "current", "bin", "codex.exe");
			if (File.Exists(standalone)) {
				return standalone;
			}

			string localApp = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"Programs", "OpenAI", "Codex", "bin", "codex.exe");
			if (File.Exists(localApp)) {
				return localApp;
			}

			string local = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "codex.exe");
			if (File.Exists(local)) {
				return local;
			}
		}

		string? onPath = ExecutableFinder.FindOnPath("codex");
		if (onPath is not null && !IsWindowsAppsAlias(onPath)) {
			return onPath;
		}

		// On POSIX, match Claude's fallback: the interactive login shell may add Codex through nvm/asdf/mise
		// even when the environment Weavie inherited at startup cannot resolve it yet.
		return OperatingSystem.IsWindows() ? null : "codex";
	}

	private static bool IsWindowsAppsAlias(string path) =>
		OperatingSystem.IsWindows()
		&& path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
}
