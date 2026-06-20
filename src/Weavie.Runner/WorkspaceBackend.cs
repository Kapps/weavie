using Weavie.Core.Processes;

namespace Weavie.Runner;

/// <summary>
/// One multi-session <c>Weavie.Headless</c> worker for a workspace — the remote backend a client connects to.
/// Since the shared <c>HostCore</c> now owns the session model, a single worker rooted at the workspace root
/// hosts every worktree session inside it (New Session creates worktrees there, exactly like local); the
/// runner does not spawn a process per session. The runner hands clients <see cref="PageUrl"/> carrying
/// <see cref="Token"/>; the worker's bridge is reachable only with that token. See docs/specs/remote-sessions.md.
/// </summary>
public sealed class WorkspaceBackend {
	/// <summary>Absolute path to the workspace (git repo) root the worker serves.</summary>
	public required string WorkspaceRoot { get; init; }

	/// <summary>The port the worker headless listens on.</summary>
	public required int Port { get; init; }

	/// <summary>The token gating the worker's bridge.</summary>
	public required string Token { get; init; }

	/// <summary>The worker's process supervisor (owns launch/restart/teardown). Set right after construction.</summary>
	public ProcessSupervisor? Supervisor { get; set; }

	/// <summary>A coarse client-facing status derived from the supervisor state.</summary>
	public string Status => Supervisor?.State switch {
		SupervisorState.Running => "running",
		SupervisorState.BackingOff => "starting",
		SupervisorState.Failed => "failed",
		SupervisorState.Idle => "stopped",
		_ => "starting",
	};

	/// <summary>The client URL for this worker against <paramref name="host"/> (the control request's host).</summary>
	public string PageUrl(string host) => $"http://{host}:{Port}/?token={Token}";
}
