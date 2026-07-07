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

		var (command, arguments) = launch.ExecutableMode == AgentExecutableMode.LoginShell
			? ResolveLoginShell(launch)
			: (launch.Command, launch.Arguments);
		return new PtyLaunch {
			Command = command,
			Arguments = arguments,
			RemoveEnvironment = launch.RemoveEnvironment,
			Environment = environment,
		};
	}

	private static (string Command, IReadOnlyList<string> Arguments) ResolveLoginShell(AgentLaunch launch) {
		string args = FormatExecArgs(launch.Arguments);
		return (LoginShellEnvironment.LoginShell(), ["-l", "-i", "-c", $"exec '{launch.Command}'{args}"]);
	}

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
}
