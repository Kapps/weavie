using Weavie.Core.Agents;
using Weavie.Core.Configuration;

namespace Weavie.Hosting;

/// <summary>Resolves the plain shell pane without giving terminal infrastructure agent-specific branches.</summary>
public sealed class ShellTerminalProcess : ITerminalProcess {
	private static readonly IReadOnlyDictionary<string, string> EmptyEnvironment =
		new Dictionary<string, string>(StringComparer.Ordinal);
	private readonly SettingsStore _settings;
	private readonly string _workspace;

	/// <summary>Creates a shell launch source rooted at <paramref name="workspace"/>.</summary>
	public ShellTerminalProcess(SettingsStore settings, string workspace) {
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentException.ThrowIfNullOrEmpty(workspace);
		_settings = settings;
		_workspace = workspace;
	}

	/// <inheritdoc/>
	public AgentLaunch ResolveLaunch() {
		string fallback = OperatingSystem.IsWindows() ? "powershell" : LoginShellEnvironment.LoginShell();
		string shell = _settings.GetString("terminal.shell") ?? fallback;
		string command = ExecutableFinder.FindOnPath(shell) ?? shell;
		string name = Path.GetFileNameWithoutExtension(command).ToLowerInvariant();
		IReadOnlyList<string> arguments = OperatingSystem.IsWindows()
			? name is "pwsh" or "powershell" ? ["-NoLogo"] : []
			: name is "zsh" or "bash" or "sh" ? ["-l", "-i"] : [];
		// OSC 7 cwd reporting (so clicked relative paths resolve against the shell's live directory) for bash/zsh,
		// unless the user turned it off. Falls back to the plain interactive args for any other shell.
		var environment = EmptyEnvironment;
		if (_settings.GetBool("terminal.shellIntegration", fallback: true) && ShellIntegration.Default().Resolve(name) is { } integration) {
			arguments = integration.Arguments;
			environment = integration.Environment;
		}

		return new AgentLaunch {
			Command = command,
			Arguments = arguments,
			WorkingDirectory = _workspace,
			RemoveEnvironment = [],
			Environment = environment,
			ExecutableMode = AgentExecutableMode.Direct,
			WorkingDirectoryMode = AgentWorkingDirectoryMode.FollowReported,
			OutputCapture = new AgentOutputCapture.Disabled(),
		};
	}

	/// <inheritdoc/>
	public void ObserveTerminalOutput(ReadOnlyMemory<byte> data) { }

	/// <inheritdoc/>
	public void ObserveTerminalInput(ReadOnlyMemory<byte> data) { }

	/// <inheritdoc/>
	public void ObserveProcessExit(AgentProcessExit exit) { }
}
