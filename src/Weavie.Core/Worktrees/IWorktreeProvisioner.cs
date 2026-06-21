namespace Weavie.Core.Worktrees;

/// <summary>Which point in a worktree's lifecycle a command runs at.</summary>
public enum WorktreeCommandPhase {
	/// <summary>Right after <c>git worktree add</c> — the user's <c>worktree.setupCommand</c>.</summary>
	Setup,

	/// <summary>Right before <c>git worktree remove</c> — the user's <c>worktree.teardownCommand</c>.</summary>
	Teardown,
}

/// <summary>
/// The outcome of running a worktree lifecycle command. <see cref="Ran"/> is false when no command was
/// configured (the empty default), in which case the phase is a no-op and counts as succeeded.
/// </summary>
public sealed record WorktreeCommandResult {
	/// <summary>Whether a command was configured and actually executed.</summary>
	public required bool Ran { get; init; }

	/// <summary>The process exit code (0 = success); meaningless when <see cref="Ran"/> is false.</summary>
	public int ExitCode { get; init; }

	/// <summary>Captured standard output.</summary>
	public string StdOut { get; init; } = "";

	/// <summary>Captured standard error.</summary>
	public string StdErr { get; init; } = "";

	/// <summary>A no-op (nothing configured) or a zero-exit run.</summary>
	public bool Succeeded => !Ran || ExitCode == 0;
}

/// <summary>A lifecycle-command event raised so the host can surface progress (toast) and output (log).</summary>
/// <param name="Phase">Setup or teardown.</param>
/// <param name="Command">The shell command being run.</param>
/// <param name="WorktreePath">The worktree the command runs in.</param>
/// <param name="Result">The outcome on completion; <c>null</c> on the start event.</param>
public readonly record struct WorktreeCommandEvent(
	WorktreeCommandPhase Phase, string Command, string WorktreePath, WorktreeCommandResult? Result);

/// <summary>
/// Runs the user-configured setup/teardown shell commands around a worktree's lifecycle: setup after a
/// worktree is created (in the background so the new session isn't blocked) and teardown before one is
/// discarded (from <see cref="WorktreeManager.RemoveAsync"/>). Implementations are one-shot process helpers,
/// exempt from <c>ProcessSupervisor</c>, and surface output rather than swallowing it.
/// </summary>
public interface IWorktreeProvisioner {
	/// <summary>Runs the configured setup command in <paramref name="worktreePath"/>; a no-op when none is set.</summary>
	Task<WorktreeCommandResult> RunSetupAsync(string worktreePath, CancellationToken ct);

	/// <summary>Runs the configured teardown command in <paramref name="worktreePath"/>; a no-op when none is set.</summary>
	Task<WorktreeCommandResult> RunTeardownAsync(string worktreePath, CancellationToken ct);
}
