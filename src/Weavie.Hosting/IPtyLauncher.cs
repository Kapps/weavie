using Weavie.Core.Configuration;
using Weavie.Core.Terminal;

namespace Weavie.Hosting;

/// <summary>
/// The platform-specific half of <see cref="TerminalController"/>: creates the PTY backend and resolves how to
/// launch claude/the shell on this OS — the only seam differing between ConPTY (Windows) and POSIX (macOS +
/// Linux). Both launchers (<see cref="PosixPtyLauncher"/>, <see cref="WindowsPtyLauncher"/>) live here.
/// </summary>
public interface IPtyLauncher {
	/// <summary>Creates a fresh PTY backend for one child process.</summary>
	ITerminal CreateTerminal();

	/// <summary>Resolves the command, arguments, and environment to spawn this controller's child.</summary>
	PtyLaunch Resolve(PtyLaunchRequest request);
}

/// <summary>The inputs a launcher reads to build a <see cref="PtyLaunch"/> — the controller's live config.</summary>
public sealed record PtyLaunchRequest {
	/// <summary>True for the claude pane (key-stripped, MCP discovery env, launcher flags); false for the plain shell.</summary>
	public required bool IsClaude { get; init; }

	/// <summary>The user settings store (resolves <c>claude.path</c> / <c>terminal.shell</c>).</summary>
	public required SettingsStore Settings { get; init; }

	/// <summary><c>--mcp-config</c> path for claude, if any.</summary>
	public string? McpConfigPath { get; init; }

	/// <summary><c>--settings</c> path for claude, if any.</summary>
	public string? SettingsFilePath { get; init; }

	/// <summary><c>--append-system-prompt-file</c> path for claude, if any.</summary>
	public string? SystemPromptFilePath { get; init; }

	/// <summary>The MCP discovery environment injected into claude (empty for the shell).</summary>
	public required IReadOnlyDictionary<string, string> ExtraEnvironment { get; init; }

	/// <summary>
	/// The claude session-resume flag pair (<c>["--resume", &lt;id&gt;]</c> or <c>["--session-id", &lt;id&gt;]</c>),
	/// already resolved by the controller from the <c>claude.resumeSession</c> policy; empty when disabled or for
	/// the shell. The launcher just appends these to claude's command line in whatever form its OS needs.
	/// </summary>
	public IReadOnlyList<string> ClaudeSessionArguments { get; init; } = [];

	/// <summary>
	/// The ordered claude flags (excluding the executable): <c>--mcp-config</c>/<c>--settings</c>/
	/// <c>--append-system-prompt-file</c> for whichever paths are set, then the session args. Both launchers share
	/// this; each renders it for its OS (flat arg list on Windows, folded into the exec string on POSIX).
	/// </summary>
	public IReadOnlyList<string> BuildClaudeArguments() {
		var args = new List<string>();
		if (!string.IsNullOrEmpty(McpConfigPath)) {
			args.Add("--mcp-config");
			args.Add(McpConfigPath);
		}

		if (!string.IsNullOrEmpty(SettingsFilePath)) {
			args.Add("--settings");
			args.Add(SettingsFilePath);
		}

		if (!string.IsNullOrEmpty(SystemPromptFilePath)) {
			args.Add("--append-system-prompt-file");
			args.Add(SystemPromptFilePath);
		}

		args.AddRange(ClaudeSessionArguments);
		return args;
	}
}

/// <summary>A resolved launch spec: what to spawn and with which environment overrides.</summary>
public sealed record PtyLaunch {
	/// <summary>The executable to launch.</summary>
	public required string Command { get; init; }

	/// <summary>Arguments passed to <see cref="Command"/>.</summary>
	public required IReadOnlyList<string> Arguments { get; init; }

	/// <summary>Environment variable names to strip from the child (e.g. <c>ANTHROPIC_API_KEY</c>).</summary>
	public required IReadOnlyList<string> RemoveEnvironment { get; init; }

	/// <summary>Environment variables to set/override on top of the inherited process environment.</summary>
	public required IReadOnlyDictionary<string, string> Environment { get; init; }
}
