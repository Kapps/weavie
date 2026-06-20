namespace Weavie.Core.Worktrees;

/// <summary>
/// An <see cref="IWorktreeProvisioner"/> that runs nothing — the required dependency for worktree
/// managers that have no lifecycle commands to run (tests, headless hosts, not-a-git-repo workspaces),
/// so callers never have to pass <c>null</c>.
/// </summary>
public sealed class NullWorktreeProvisioner : IWorktreeProvisioner {
	/// <summary>The shared instance (the provisioner is stateless).</summary>
	public static NullWorktreeProvisioner Instance { get; } = new();

	private NullWorktreeProvisioner() {
	}

	/// <inheritdoc/>
	public Task<WorktreeCommandResult> RunSetupAsync(string worktreePath, CancellationToken ct) =>
		Task.FromResult(new WorktreeCommandResult { Ran = false });

	/// <inheritdoc/>
	public Task<WorktreeCommandResult> RunTeardownAsync(string worktreePath, CancellationToken ct) =>
		Task.FromResult(new WorktreeCommandResult { Ran = false });
}
