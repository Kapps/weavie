using Weavie.Core.Processes;

namespace Weavie.Core.Worktrees;

/// <summary>
/// Runs the configured <c>worktree.setupCommand</c> / <c>worktree.teardownCommand</c> through the platform shell
/// in the worktree, capturing output. Command strings are read live through the injected getters, so an edited setting takes effect next run; failures are reported via <see cref="Finished"/>, never swallowed.
/// </summary>
public sealed class ShellWorktreeProvisioner : IWorktreeProvisioner {
	private readonly Func<string?> _setupCommand;
	private readonly Func<string?> _teardownCommand;

	/// <summary>
	/// Creates a provisioner reading the setup/teardown command strings from <paramref name="setupCommand"/>
	/// and <paramref name="teardownCommand"/>, evaluated fresh on each run.
	/// </summary>
	public ShellWorktreeProvisioner(Func<string?> setupCommand, Func<string?> teardownCommand) {
		ArgumentNullException.ThrowIfNull(setupCommand);
		ArgumentNullException.ThrowIfNull(teardownCommand);
		_setupCommand = setupCommand;
		_teardownCommand = teardownCommand;
	}

	/// <summary>Raised just before a configured command runs (<see cref="WorktreeCommandEvent.Result"/> is null).</summary>
	public event Action<WorktreeCommandEvent>? Starting;

	/// <summary>Raised when a command finishes, carrying its <see cref="WorktreeCommandResult"/>.</summary>
	public event Action<WorktreeCommandEvent>? Finished;

	/// <inheritdoc/>
	public Task<WorktreeCommandResult> RunSetupAsync(string worktreePath, CancellationToken ct) =>
		RunAsync(WorktreeCommandPhase.Setup, _setupCommand(), worktreePath, ct);

	/// <inheritdoc/>
	public Task<WorktreeCommandResult> RunTeardownAsync(string worktreePath, CancellationToken ct) =>
		RunAsync(WorktreeCommandPhase.Teardown, _teardownCommand(), worktreePath, ct);

	private async Task<WorktreeCommandResult> RunAsync(
		WorktreeCommandPhase phase, string? command, string worktreePath, CancellationToken ct) {
		ArgumentException.ThrowIfNullOrEmpty(worktreePath);
		if (string.IsNullOrWhiteSpace(command)) {
			return new WorktreeCommandResult { Ran = false };
		}

		Starting?.Invoke(new WorktreeCommandEvent(phase, command, worktreePath, null));
		var result = await ExecuteAsync(command, worktreePath, ct).ConfigureAwait(false);
		Finished?.Invoke(new WorktreeCommandEvent(phase, command, worktreePath, result));
		return result;
	}

	// A shell that couldn't start reports as a failed run (ToolProcess's exit -1), never a throw, so setup
	// can't crash session creation nor teardown block removal.
	private static async Task<WorktreeCommandResult> ExecuteAsync(string command, string worktreePath, CancellationToken ct) {
		var result = await ToolProcess.RunAsync(new ToolProcessRequest(
			OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
			[OperatingSystem.IsWindows() ? "/c" : "-c", command],
			new Dictionary<string, string>(),
			worktreePath), ct).ConfigureAwait(false);
		return new WorktreeCommandResult {
			Ran = true,
			ExitCode = result.ExitCode,
			StdOut = result.StdOut,
			StdErr = result.StdErr,
		};
	}
}
