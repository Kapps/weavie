using Weavie.Core.Agents;
using Weavie.Core.Terminal;

namespace Weavie.Hosting;

/// <summary>The platform-specific PTY factory and renderer for a provider-neutral logical launch.</summary>
public interface IPtyLauncher {
	/// <summary>Creates a fresh PTY backend for one child process.</summary>
	ITerminal CreateTerminal();

	/// <summary>Renders a logical launch for this platform.</summary>
	PtyLaunch Resolve(AgentLaunch launch);
}

/// <summary>A platform-rendered PTY launch.</summary>
public sealed record PtyLaunch {
	/// <summary>The executable to launch.</summary>
	public required string Command { get; init; }

	/// <summary>Arguments passed to <see cref="Command"/>.</summary>
	public required IReadOnlyList<string> Arguments { get; init; }

	/// <summary>Environment variable names stripped from the child.</summary>
	public required IReadOnlyList<string> RemoveEnvironment { get; init; }

	/// <summary>Environment variables added or overridden for the child.</summary>
	public required IReadOnlyDictionary<string, string> Environment { get; init; }
}
