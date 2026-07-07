using Weavie.Core.Agents;
using Weavie.Core.Terminal;

namespace Weavie.Hosting;

/// <summary>The Windows ConPTY backend and command-shim renderer for provider-neutral logical launches.</summary>
public sealed class WindowsPtyLauncher : IPtyLauncher {
	/// <inheritdoc/>
	public ITerminal CreateTerminal() => new WindowsConPtyTerminal();

	/// <inheritdoc/>
	public PtyLaunch Resolve(AgentLaunch launch) {
		ArgumentNullException.ThrowIfNull(launch);
		var (command, arguments) = ResolveCommand(launch);
		return new PtyLaunch {
			Command = command,
			Arguments = arguments,
			RemoveEnvironment = launch.RemoveEnvironment,
			Environment = launch.Environment,
		};
	}

	private static (string Command, IReadOnlyList<string> Arguments) ResolveCommand(AgentLaunch launch) {
		string ext = Path.GetExtension(launch.Command).ToLowerInvariant();
		if (ext is ".cmd" or ".bat") {
			string comspec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
			return (comspec, ["/c", launch.Command, .. launch.Arguments]);
		}
		return (launch.Command, launch.Arguments);
	}
}
