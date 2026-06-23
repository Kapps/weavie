using System.ComponentModel;
using System.Diagnostics;
using System.Text;

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

	private static async Task<WorktreeCommandResult> ExecuteAsync(string command, string worktreePath, CancellationToken ct) {
		var info = new ProcessStartInfo {
			FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
			WorkingDirectory = worktreePath,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
		};
		info.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
		info.ArgumentList.Add(command);

		using var process = new Process { StartInfo = info };
		try {
			process.Start();
		} catch (Win32Exception ex) {
			// Shell couldn't start: surface a failed run, not a throw, so setup can't crash session creation nor teardown block removal.
			return new WorktreeCommandResult {
				Ran = true,
				ExitCode = -1,
				StdErr = $"Unable to start the shell to run the command: {ex.Message}",
			};
		}

		var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
		var stderrTask = process.StandardError.ReadToEndAsync(ct);
		await process.WaitForExitAsync(ct).ConfigureAwait(false);
		string stdout = await stdoutTask.ConfigureAwait(false);
		string stderr = await stderrTask.ConfigureAwait(false);
		return new WorktreeCommandResult {
			Ran = true,
			ExitCode = process.ExitCode,
			StdOut = stdout,
			StdErr = stderr,
		};
	}
}
