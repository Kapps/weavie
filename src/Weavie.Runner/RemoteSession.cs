using Weavie.Core.Processes;

namespace Weavie.Runner;

/// <summary>
/// One live remote session: a git worktree plus the supervised <c>Weavie.Headless</c> worker serving it.
/// The runner hands clients <see cref="PageUrl"/> (host filled in per request) carrying <see cref="Token"/>;
/// the worker is reachable only with that token. State is derived from the worker's <see cref="Supervisor"/>.
/// </summary>
public sealed class RemoteSession {
	/// <summary>Short opaque id used in control-plane routes.</summary>
	public required string Id { get; init; }

	/// <summary>The branch (and worktree) name this session works on.</summary>
	public required string Branch { get; init; }

	/// <summary>Absolute path to the worktree the worker is rooted at.</summary>
	public required string WorktreePath { get; init; }

	/// <summary>The port the worker headless listens on.</summary>
	public required int Port { get; init; }

	/// <summary>The per-session token gating the worker's page + bridge.</summary>
	public required string Token { get; init; }

	/// <summary>When the session was created.</summary>
	public DateTimeOffset CreatedAtUtc { get; } = DateTimeOffset.UtcNow;

	/// <summary>The worker's process supervisor (owns launch/restart/teardown). Set right after construction.</summary>
	public ProcessSupervisor? Supervisor { get; set; }

	/// <summary>A coarse client-facing status string derived from the supervisor state.</summary>
	public string Status => Supervisor?.State switch {
		SupervisorState.Running => "running",
		SupervisorState.BackingOff => "starting",
		SupervisorState.Failed => "failed",
		SupervisorState.Idle => "stopped",
		_ => "starting",
	};

	/// <summary>The client URL for this session against <paramref name="host"/> (the control request's host).</summary>
	public string PageUrl(string host) => $"http://{host}:{Port}/?token={Token}";
}
