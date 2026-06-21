using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Weavie.Core.Worktrees;

/// <summary>
/// Runs the configured <c>worktree.setupCommand</c> / <c>worktree.teardownCommand</c> through the platform
/// shell (<c>cmd /c</c> on Windows, <c>/bin/sh -c</c> elsewhere) with the worktree as the working directory,
/// capturing output. Each run is a short-lived one-shot process, exempt from <c>ProcessSupervisor</c>. The
/// command strings are read live through the injected getters, so editing the setting takes effect on the
/// next run. Progress and results surface via <see cref="Starting"/> / <see cref="Finished"/>; a failed
/// command is reported, never swallowed.
/// </summary>
public sealed class ShellWorktreeProvisioner : IWorktreeProvisioner {
	private readonly Func<string?> _setupCommand;
	private readonly Func<string?> _teardownCommand;

	/// <summary>
	/// Creates a provisioner reading the setup/teardown command strings from <paramref name="setupCommand"/>
	/// and <paramref name="teardownCommand"/> (typically <c>() => settings.GetString("worktree.setupCommand")</c>),
	/// evaluated fresh on each run.
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
			// The shell itself couldn't start — surface it as a failed run rather than throwing, so a
			// background setup can't crash session creation and a teardown can't block removal.
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
