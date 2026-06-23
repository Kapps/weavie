namespace Weavie.Core.Worktrees;

/// <summary>An <see cref="IWorktreeProvisioner"/> that runs nothing, so callers with no lifecycle commands never pass <c>null</c>.</summary>
public sealed class NullWorktreeProvisioner : IWorktreeProvisioner {
	/// <summary>The shared stateless instance.</summary>
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
