using Weavie.Core.Agents;
using Weavie.Core.Terminal;

namespace Weavie.Hosting;

/// <summary>The POSIX PTY backend and renderer for provider-neutral logical launches.</summary>
public sealed class PosixPtyLauncher : IPtyLauncher {
	/// <inheritdoc/>
	public ITerminal CreateTerminal() => new PosixPtyTerminal();

	/// <inheritdoc/>
	public PtyLaunch Resolve(AgentLaunch launch) {
		ArgumentNullException.ThrowIfNull(launch);
		var environment = new Dictionary<string, string>(StringComparer.Ordinal) {
			["TERM"] = "xterm-256color",
			["COLORTERM"] = "truecolor",
		};
		foreach (var (key, value) in launch.Environment) {
			environment[key] = value;
		}

		var (command, arguments) = launch.ExecutableMode switch {
			AgentExecutableMode.Direct => (launch.Command, launch.Arguments),
			AgentExecutableMode.SearchPath => ("/usr/bin/env", [launch.Command, .. launch.Arguments]),
			AgentExecutableMode.LoginShell => ResolveLoginShell(launch),
			_ => throw new InvalidOperationException($"Unknown executable mode '{launch.ExecutableMode}'."),
		};
		return new PtyLaunch {
			Command = command,
			Arguments = arguments,
			RemoveEnvironment = launch.RemoveEnvironment,
			Environment = environment,
		};
	}

	private static (string Command, IReadOnlyList<string> Arguments) ResolveLoginShell(AgentLaunch launch) {
		string command = string.Join(
			' ',
			launch.Arguments.Prepend(launch.Command).Select(ShellQuote));
		return (LoginShellEnvironment.LoginShell(), ["-l", "-i", "-c", $"exec {command}"]);
	}

	// Single-quoted, with embedded quotes closed and re-opened ('\'') — the only POSIX form that keeps an
	// arbitrary argument (a prompt with an apostrophe, spaces, newlines, or $) literal for the shell.
	private static string ShellQuote(string argument) =>
		$"'{argument.Replace("'", "'\\''", StringComparison.Ordinal)}'";
}
