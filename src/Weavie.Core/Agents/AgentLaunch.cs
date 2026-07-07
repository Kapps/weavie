namespace Weavie.Core.Agents;

/// <summary>How a PTY child resolves its executable on POSIX hosts.</summary>
public enum AgentExecutableMode {
	/// <summary>Launch the executable directly.</summary>
	Direct,

	/// <summary>Resolve the executable through the user's interactive login shell.</summary>
	LoginShell,
}

/// <summary>How a terminal chooses the working directory for a subsequent launch.</summary>
public enum AgentWorkingDirectoryMode {
	/// <summary>Always use the launch's configured working directory.</summary>
	Fixed,

	/// <summary>Prefer the last valid OSC 7 directory reported by the child.</summary>
	FollowReported,
}

/// <summary>A provider-requested PTY output capture.</summary>
public abstract record AgentOutputCapture {
	private AgentOutputCapture() { }

	/// <summary>No provider capture.</summary>
	public sealed record Disabled : AgentOutputCapture;

	/// <summary>Replace <paramref name="Path"/> with this launch's raw PTY output.</summary>
	public sealed record File(string Path) : AgentOutputCapture;
}

/// <summary>A provider-neutral logical child launch, resolved afresh for every supervised start.</summary>
public sealed record AgentLaunch {
	/// <summary>The executable before platform-specific wrapping.</summary>
	public required string Command { get; init; }

	/// <summary>Ordered arguments passed to <see cref="Command"/>.</summary>
	public required IReadOnlyList<string> Arguments { get; init; }

	/// <summary>The child working directory.</summary>
	public required string WorkingDirectory { get; init; }

	/// <summary>Environment variable names removed from the inherited environment.</summary>
	public required IReadOnlyList<string> RemoveEnvironment { get; init; }

	/// <summary>Environment additions and overrides.</summary>
	public required IReadOnlyDictionary<string, string> Environment { get; init; }

	/// <summary>Whether the platform launches directly or through an interactive login shell.</summary>
	public required AgentExecutableMode ExecutableMode { get; init; }

	/// <summary>Whether restarts stay rooted or follow the last reported cwd.</summary>
	public required AgentWorkingDirectoryMode WorkingDirectoryMode { get; init; }

	/// <summary>Provider-owned raw-output capture behavior.</summary>
	public required AgentOutputCapture OutputCapture { get; init; }
}

/// <summary>A PTY exit observed before the process supervisor applies its restart policy.</summary>
public sealed record AgentProcessExit {
	/// <summary>The child exit code.</summary>
	public required int ExitCode { get; init; }

	/// <summary>True when the child exited while the supervisor still considered it running.</summary>
	public required bool Unexpected { get; init; }
}
