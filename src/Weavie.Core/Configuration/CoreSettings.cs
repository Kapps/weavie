namespace Weavie.Core.Configuration;

/// <summary>
/// Registers Weavie's built-in settings and is the home for the per-platform resolution logic that
/// used to live inline in the two <c>TerminalController</c>s (workspace / shell / claude discovery).
/// Centralizing it here means Windows and macOS share one path: the registry's
/// <see cref="SettingDefinition.ComputeDefault"/> and <see cref="SettingDefinition.Validate"/>.
/// </summary>
public static class CoreSettings {
	/// <summary>Builds a registry pre-loaded with <c>workspace</c>, <c>terminal.shell</c>, and <c>claude.path</c>.</summary>
	public static SettingsRegistry CreateRegistry() {
		var registry = new SettingsRegistry();
		Register(registry);
		return registry;
	}

	/// <summary>Creates a store backed by the core registry over <paramref name="filePath"/> (default <c>~/.weavie/settings.toml</c>).</summary>
	public static SettingsStore CreateStore(string? filePath = null, bool enableWatcher = true) =>
		new(CreateRegistry(), filePath, enableWatcher);

	/// <summary>Registers the three built-in settings into <paramref name="registry"/>.</summary>
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

		var shell = Environment.GetEnvironmentVariable("SHELL");
		return !string.IsNullOrEmpty(shell) && File.Exists(shell) ? shell : "/bin/zsh";
	}

	/// <summary>
	/// The auto-detected claude binary: a <c>claude</c> on PATH, else the native-installer location, else
	/// bare <c>claude</c> (let the launcher search PATH). The host applies its own launch shim on top.
	/// </summary>
	private static object? DefaultClaudePath() {
		var onPath = ExecutableFinder.FindOnPath("claude");
		if (onPath is not null) {
			return onPath;
		}

		if (OperatingSystem.IsWindows()) {
			var local = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
			return File.Exists(local) ? local : "claude";
		}

		return "claude";
	}
}
