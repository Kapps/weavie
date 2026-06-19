namespace Weavie.Core.Configuration;

/// <summary>
/// Registers Weavie's built-in settings and owns the per-platform resolution logic for workspace /
/// shell / claude discovery, so Windows and macOS share one path through the registry's
/// <see cref="SettingDefinition.ComputeDefault"/> and <see cref="SettingDefinition.Validate"/>.
/// </summary>
public static class CoreSettings {
	/// <summary>Builds a registry pre-loaded with the built-in settings (workspace, shell, claude path, fonts, diagnostics).</summary>
	public static SettingsRegistry CreateRegistry() {
		var registry = new SettingsRegistry();
		Register(registry);
		return registry;
	}

	/// <summary>Creates a store backed by the core registry over <paramref name="filePath"/> (default <c>~/.weavie/settings.toml</c>).</summary>
	public static SettingsStore CreateStore(string? filePath = null, bool enableWatcher = true) =>
		new(CreateRegistry(), filePath, enableWatcher);

	/// <summary>Registers the built-in settings (workspace, shell, claude path, fonts, diagnostics) into <paramref name="registry"/>.</summary>
	public static void Register(SettingsRegistry registry) {
		ArgumentNullException.ThrowIfNull(registry);

		registry.Register(new SettingDefinition {
			Key = "workspace",
			Kind = SettingKind.Path,
			Description = "Directory Claude and the terminal open in (the IDE workspace).",
			Aliases = ["workspace", "working directory", "project folder"],
			Apply = ApplyMode.RestartRequired,
			ComputeDefault = static () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
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
			Key = "claude.path",
			Kind = SettingKind.Path,
			Description = "Path to the claude binary (auto-detected when unset).",
			Aliases = ["claude", "claude binary", "claude path"],
			Apply = ApplyMode.NextSession,
			ComputeDefault = DefaultClaudePath,
		});

		registry.Register(new SettingDefinition {
			Key = "claude.permissionMode",
			Kind = SettingKind.String,
			Description = "How Claude's tool calls are handled: 'default' opens an editable diff you Keep or "
				+ "Reject per edit (Bash and other commands ask in the terminal); 'acceptEdits' applies edits "
				+ "automatically without prompting (Bash still asks); 'bypassPermissions' auto-allows every "
				+ "tool, including Bash, with no prompts. Changes are still recorded in every mode. Takes "
				+ "effect on the next tool call.",
			Aliases = ["permission mode", "edit mode", "accept edits", "auto accept edits", "stop asking to edit",
				"bypass permissions", "yolo mode", "allow all", "skip permissions"],
			AllowedValues = ["default", "acceptEdits", "bypassPermissions"],
			Apply = ApplyMode.Live,
			Default = "default",
		});

		FontSettings.Register(registry);
		ThemeSettings.Register(registry);

		registry.Register(new SettingDefinition {
			Key = "diagnostics.startupTiming",
			Kind = SettingKind.Bool,
			Description = "Log startup phase timings (window→navigate on the host, navigate→shell→editor "
				+ "in the web app) to the console. Off by default; for diagnosing launch latency.",
			Aliases = ["startup timing", "launch timing", "boot timing", "startup profiling"],
			// Captured during launch, so a change only takes effect on the next start.
			Apply = ApplyMode.RestartRequired,
			Default = false,
		});
	}

	/// <summary>
	/// The system-suggested shell: Windows prefers PowerShell 7 (<c>pwsh</c>) then Windows PowerShell;
	/// Unix uses <c>$SHELL</c> then <c>/bin/zsh</c>. This is the lowest-precedence layer — the value an
	/// env var or the user file overrides.
	/// </summary>
	private static object? DefaultShell() {
		if (OperatingSystem.IsWindows()) {
			return ExecutableFinder.FindOnPath("pwsh") is not null ? "pwsh" : "powershell";
		}

		string? shell = Environment.GetEnvironmentVariable("SHELL");
		return !string.IsNullOrEmpty(shell) && File.Exists(shell) ? shell : "/bin/zsh";
	}

	/// <summary>
	/// The auto-detected claude binary: a <c>claude</c> on PATH, else the native-installer location, else
	/// bare <c>claude</c> (let the launcher search PATH). The host applies its own launch shim on top.
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
