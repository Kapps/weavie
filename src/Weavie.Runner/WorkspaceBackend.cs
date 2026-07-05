using Weavie.Core.Processes;

namespace Weavie.Runner;

/// <summary>
/// One multi-session <c>Weavie.Headless</c> worker for a workspace — the remote backend a client connects to.
/// A single worker hosts every worktree session via the shared <c>HostCore</c> (no process per session);
/// <see cref="Token"/> alone unlocks its bridge. The client-facing URL is built by the <see cref="ITlsFront"/>,
/// which owns the scheme/host. See docs/specs/remote-sessions.md and docs/specs/tls-on-the-runner.md.
/// </summary>
public sealed class WorkspaceBackend {
	private volatile int _port;

	/// <summary>Absolute path to the workspace (git repo) root the worker serves.</summary>
	public required string WorkspaceRoot { get; init; }

	/// <summary>
	/// The port the worker headless listens on. Mutable: re-rolled on a restart that hit a bind conflict.
	/// Backed by a volatile field — the write comes from the supervisor's restart callback, and reads (e.g.
	/// <c>ControlUrl</c>/<c>WorkerPageUrl</c>) happen on ASP.NET request threads with no shared lock.
	/// </summary>
	public required int Port {
		get => _port;
		set => _port = value;
	}

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
}
