using Weavie.Core.Configuration;
using Weavie.Core.Terminal;

namespace Weavie.Hosting;

/// <summary>
/// The POSIX (macOS + Linux) PTY launcher: a <see cref="PosixPtyTerminal"/> backend, claude wrapped in a
/// login shell so PATH/env resolve regardless of how the app was started (a Finder/<c>open</c> launch
/// inherits a bare environment), and the plain shell resolved from the <c>terminal.shell</c> setting.
/// Injects <c>TERM</c>/<c>COLORTERM</c> so xterm.js colour + capability detection work even when the host
/// inherited no <c>TERM</c> at all. The Windows sibling lives in Weavie.Win.
/// </summary>
public sealed class PosixPtyLauncher : IPtyLauncher {
	/// <inheritdoc/>
	public ITerminal CreateTerminal() => new PosixPtyTerminal();

	/// <inheritdoc/>
	public PtyLaunch Resolve(PtyLaunchRequest request) {
		ArgumentNullException.ThrowIfNull(request);

		// The web pane is xterm.js, which emulates xterm-256color and renders 24-bit colour. Tell the child
		// explicitly so colour + capability detection work no matter how the host app was launched.
		var environment = new Dictionary<string, string>(StringComparer.Ordinal) {
			["TERM"] = "xterm-256color",
			["COLORTERM"] = "truecolor",
		};
		if (request.IsClaude) {
			foreach (var (key, value) in request.ExtraEnvironment) {
				environment[key] = value;
			}
		}

		var (command, arguments) = request.IsClaude ? ResolveClaude(request) : ResolveShell(request.Settings);
		return new PtyLaunch {
			Command = command,
			Arguments = arguments,
			// Only the claude session needs the key stripped (interactive CLI = full plan, never the SDK).
			RemoveEnvironment = request.IsClaude ? ["ANTHROPIC_API_KEY"] : [],
			Environment = environment,
		};
	}

	/// <summary>
	/// Launches claude through a POSIX login shell (for full PATH/env) that execs the <c>claude.path</c>
	/// setting — <c>-l</c> for the login environment, <c>-c "exec &lt;claude&gt;"</c> to replace the shell.
	/// </summary>
	private static (string Command, IReadOnlyList<string> Arguments) ResolveClaude(PtyLaunchRequest request) {
		string claude = request.Settings.GetString("claude.path") ?? "claude";
		string mcp = string.IsNullOrEmpty(request.McpConfigPath) ? string.Empty : $" --mcp-config '{request.McpConfigPath}'";
		string settings = string.IsNullOrEmpty(request.SettingsFilePath) ? string.Empty : $" --settings '{request.SettingsFilePath}'";
		string systemPrompt = string.IsNullOrEmpty(request.SystemPromptFilePath) ? string.Empty : $" --append-system-prompt-file '{request.SystemPromptFilePath}'";
		// Session resume (--resume/--session-id <id>), already resolved by the controller; folded into the exec
		// string with the id single-quoted like the other paths. Empty when resume is off.
		string session = FormatExecArgs(request.ClaudeSessionArguments);
		return (LoginShell(), ["-l", "-c", $"exec '{claude}'{mcp}{settings}{systemPrompt}{session}"]);
	}

	/// <summary>Folds a resolved arg list into the login-shell exec string: flags as-is, values single-quoted.</summary>
	private static string FormatExecArgs(IReadOnlyList<string> args) {
		if (args.Count == 0) {
			return string.Empty;
		}

		var sb = new System.Text.StringBuilder();
		foreach (string arg in args) {
			sb.Append(' ').Append(arg.StartsWith('-') ? arg : $"'{arg}'");
		}

		return sb.ToString();
	}

	/// <summary>
	/// Resolves the plain-terminal shell from the <c>terminal.shell</c> setting to a launchable path,
	/// passing <c>-l -i</c> only to POSIX login shells (zsh/bash/sh) so the prompt + rc files load; other
	/// shells (nushell, fish, …) open at their prompt with no flags.
	/// </summary>
	private static (string Command, IReadOnlyList<string> Arguments) ResolveShell(SettingsStore settings) {
		string shell = settings.GetString("terminal.shell") ?? LoginShell();
		string command = ExecutableFinder.FindOnPath(shell) ?? shell;
		string name = Path.GetFileNameWithoutExtension(command);
		IReadOnlyList<string> arguments = name is "zsh" or "bash" or "sh" ? ["-l", "-i"] : [];
		return (command, arguments);
	}

	/// <summary>
	/// The system login shell used to wrap claude: <c>$SHELL</c> if it exists, else the per-OS default
	/// (<c>/bin/zsh</c> on macOS, <c>/bin/bash</c> elsewhere).
	/// </summary>
	private static string LoginShell() {
		string? shell = Environment.GetEnvironmentVariable("SHELL");
		if (!string.IsNullOrEmpty(shell) && File.Exists(shell)) {
			return shell;
		}

		return OperatingSystem.IsMacOS() ? "/bin/zsh" : "/bin/bash";
	}
}
