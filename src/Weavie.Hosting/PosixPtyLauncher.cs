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
	/// Launches claude through a POSIX login shell (<c>-l -i -c "exec &lt;claude.path&gt;"</c>) for full PATH/env.
	/// <c>-i</c> is essential: a Finder-launched <c>.app</c> inherits launchd's minimal PATH, and a login-only
	/// shell sources <c>~/.zprofile</c> but not <c>~/.zshrc</c> — where PATH entries (and claude's
	/// <c>~/.local/bin</c>) usually live — so without it claude (and the node/git/rg it spawns) isn't found.
	/// </summary>
	private static (string Command, IReadOnlyList<string> Arguments) ResolveClaude(PtyLaunchRequest request) {
		string claude = request.Settings.GetString("claude.path") ?? "claude";
		// Flags (--mcp-config/--settings/--append-system-prompt-file) and the session-resume args are assembled
		// once on the request, then folded into the exec string here with each value single-quoted.
		string args = FormatExecArgs(request.BuildClaudeArguments());
		return (LoginShellPath.LoginShell(), ["-l", "-i", "-c", $"exec '{claude}'{args}"]);
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
	/// Resolves the plain-terminal shell (<c>terminal.shell</c>) to a launchable path, passing <c>-l -i</c> only to
	/// POSIX login shells (zsh/bash/sh) so rc files load; others (nushell, fish) open with no flags.
	/// </summary>
	private static (string Command, IReadOnlyList<string> Arguments) ResolveShell(SettingsStore settings) {
		string shell = settings.GetString("terminal.shell") ?? LoginShellPath.LoginShell();
		string command = ExecutableFinder.FindOnPath(shell) ?? shell;
		string name = Path.GetFileNameWithoutExtension(command);
		IReadOnlyList<string> arguments = name is "zsh" or "bash" or "sh" ? ["-l", "-i"] : [];
		return (command, arguments);
	}
}
