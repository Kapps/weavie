using System.Diagnostics;
using Weavie.Core.Processes;

namespace Weavie.WorktreeServe;

internal sealed class SupervisedProcess : IDisposable {
	private readonly ProcessStartInfo _startInfo;
	private readonly Action<string> _stdout;
	private readonly Action<string> _stderr;
	private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly ProcessSupervisor _supervisor;
	private Process? _current;

	public SupervisedProcess(
		string name,
		ProcessStartInfo startInfo,
		Action<string> stdout,
		Action<string> stderr) {
		_startInfo = startInfo;
		_stdout = stdout;
		_stderr = stderr;
		_supervisor = new ProcessSupervisor(
			name,
			Start,
			StopCurrent,
			new SupervisionOptions { Policy = RestartPolicy.Never },
			entry => Console.WriteLine($"[worktree-serve] {entry.Name}: {entry.Message}"),
			clock: null);
	}

	public Task<int> Completion => _completion.Task;

	public void Start() => _supervisor.Start();

	public void Stop() => _supervisor.Stop();

	public void Dispose() => _supervisor.Dispose();

	private void Start(SupervisedLaunch launch) {
		try {
			var process = new Process { StartInfo = _startInfo, EnableRaisingEvents = true };
			_current = process;
			process.Start();
			_ = ObserveAsync(process, launch);
		} catch (Exception ex) {
			_completion.TrySetException(ex);
			throw;
		}
	}

	private async Task ObserveAsync(Process process, SupervisedLaunch launch) {
		using var pumps = new CancellationTokenSource();
		var output = PumpAsync(process.StandardOutput, _stdout, pumps.Token);
		var error = PumpAsync(process.StandardError, _stderr, pumps.Token);
		await process.WaitForExitAsync().ConfigureAwait(false);
		int exitCode = process.ExitCode;
		_completion.TrySetResult(exitCode);
		launch.NotifyExited(exitCode);
		pumps.Cancel();
		process.Dispose();
		await Task.WhenAll(output, error).ConfigureAwait(false);
	}

	private static async Task PumpAsync(StreamReader reader, Action<string> sink, CancellationToken cancellationToken) {
		try {
			while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line) {
				sink(line);
			}
		} catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) {
			// The root process owns lifecycle completion; descendants may retain inherited pipe handles.
		}
	}

	private void StopCurrent() {
		try {
			if (_current is { HasExited: false }) {
				_current.Kill(entireProcessTree: true);
			}
		} catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) {
		}
	}
}
