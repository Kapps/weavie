using Weavie.Core.Configuration;
using Weavie.Core.Terminal;

namespace Weavie.Hosting;

/// <summary>
/// The Windows PTY launcher: a <see cref="WindowsConPtyTerminal"/> (ConPTY) backend, claude resolved from the
/// <c>claude.path</c> setting, and the shell from <c>terminal.shell</c>. ConPTY ignores <c>TERM</c>, so no
/// colour env is injected. The POSIX sibling is <see cref="PosixPtyLauncher"/>; both live here so every host —
/// the native Windows shell and the headless worker running on Windows — picks the right one per OS.
/// </summary>
public sealed class WindowsPtyLauncher : IPtyLauncher {
	/// <inheritdoc/>
	public ITerminal CreateTerminal() => new WindowsConPtyTerminal();

	/// <inheritdoc/>
	public PtyLaunch Resolve(PtyLaunchRequest request) {
		ArgumentNullException.ThrowIfNull(request);
		var (command, arguments) = request.IsClaude ? ResolveClaude(request) : ResolveShell(request.Settings);
		return new PtyLaunch {
			Command = command,
			Arguments = arguments,
			// Only the claude session needs the key stripped + the MCP discovery env injected.
			RemoveEnvironment = request.IsClaude ? ["ANTHROPIC_API_KEY"] : [],
			Environment = request.IsClaude
				? request.ExtraEnvironment
				: new Dictionary<string, string>(StringComparer.Ordinal),
		};
	}

	/// <summary>
	/// Resolves how to launch claude from <c>claude.path</c>: a <c>.cmd</c>/<c>.bat</c> shim runs through
	/// cmd.exe; a native exe (or a bare <c>claude</c> found on PATH) launches directly.
	/// </summary>
	private static (string Command, IReadOnlyList<string> Arguments) ResolveClaude(PtyLaunchRequest request) {
		string claude = request.Settings.GetString("claude.path") ?? "claude";
		var claudeArgs = request.BuildClaudeArguments();
		string ext = Path.GetExtension(claude).ToLowerInvariant();
		if (ext is ".cmd" or ".bat") {
			string comspec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
			return (comspec, ["/c", claude, .. claudeArgs]);
		}

		return (claude, claudeArgs);
	}

	/// <summary>
	/// Resolves the plain-terminal shell from <c>terminal.shell</c>, passing <c>-NoLogo</c> only to
	/// PowerShell to suppress its banner; other shells open with no flags.
	/// </summary>
	private static (string Command, IReadOnlyList<string> Arguments) ResolveShell(SettingsStore settings) {
		string shell = settings.GetString("terminal.shell") ?? "powershell";
		string command = ExecutableFinder.FindOnPath(shell) ?? shell;
		string name = Path.GetFileNameWithoutExtension(command).ToLowerInvariant();
		IReadOnlyList<string> arguments = name is "pwsh" or "powershell" ? ["-NoLogo"] : [];
		return (command, arguments);
	}
}
