using System.Diagnostics;

namespace Weavie.Core.Processes;

/// <summary>
/// A redirected child whose macOS session is isolated before its lifetime is exposed to callers. Its system temp
/// path is a <see cref="ChildTempScope"/> it holds until it is reaped, released before its exit is seen.
/// </summary>
public sealed partial class OwnedProcess : IDisposable {
	private readonly Process? _managed;
	private readonly ChildTempScope _temp;
	private readonly Lock _gate = new();
	private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

	private OwnedProcess(Process process, ChildTempScope temp) {
		_managed = process;
		_temp = temp;
		Id = process.Id;
		StandardInput = process.StartInfo.RedirectStandardInput ? process.StandardInput : StreamWriter.Null;
		StandardOutput = process.StandardOutput;
		StandardError = process.StandardError;
		_ = ObserveManagedExitAsync(process);
	}

	/// <summary>Starts a child in a temp directory of its own; see <see cref="Start(ProcessStartInfo, ChildTempScope)"/>.</summary>
	public static OwnedProcess Start(ProcessStartInfo info) {
		using var temp = ChildTempScope.Create();
		return Start(info, temp);
	}

	/// <summary>
	/// Starts a child with redirected output in <paramref name="temp"/>; macOS uses an atomic native spawn into a new
	/// session.
	/// </summary>
	public static OwnedProcess Start(ProcessStartInfo info, ChildTempScope temp) {
		ArgumentNullException.ThrowIfNull(info);
		ArgumentNullException.ThrowIfNull(temp);
		if (info.UseShellExecute || !info.RedirectStandardOutput || !info.RedirectStandardError) {
			throw new ArgumentException("Owned children require shell execution disabled and stdout/stderr redirected.", nameof(info));
		}
		temp.Acquire();
		OwnedProcess child;
		try {
			ChildTemp.Point(info, temp.Directory);
			child = OperatingSystem.IsWindows()
				? new OwnedProcess(Process.Start(info) ?? throw new IOException($"Could not start '{info.FileName}'."), temp)
				: StartUnix(info, temp);
		} catch {
			temp.Release();
			throw;
		}
		Console.WriteLine($"[process] started host={Environment.ProcessId} child={child.Id} executable={info.FileName}");
		return child;
	}

	/// <summary>The owned child's process id.</summary>
	public int Id { get; }
	/// <summary>The child's input stream.</summary>
	public StreamWriter StandardInput { get; }
	/// <summary>The child's output stream.</summary>
	public StreamReader StandardOutput { get; }
	/// <summary>The child's error stream.</summary>
	public StreamReader StandardError { get; }
	/// <summary>Whether the child has been reaped.</summary>
	public bool HasExited => _exit.Task.IsCompleted;
	/// <summary>The reaped exit code; throws while the child is running.</summary>
	public int ExitCode => HasExited ? _exit.Task.GetAwaiter().GetResult() : throw new InvalidOperationException("The child is still running.");

	/// <summary>Waits until the child is reaped.</summary>
	public Task WaitForExitAsync(CancellationToken ct = default) => _exit.Task.WaitAsync(ct);
	/// <summary>Waits synchronously until the child is reaped.</summary>
	public void WaitForExit() => _exit.Task.GetAwaiter().GetResult();
	/// <summary>Delivers the owned child's exit to its supervisor.</summary>
	public async Task ObserveExitAsync(Action<int> exited) => exited(await _exit.Task.ConfigureAwait(false));

	/// <summary>Terminates the owned child and, when requested, its descendants.</summary>
	public void Kill(bool entireProcessTree) {
		lock (_gate) {
			if (HasExited) return;
			Console.WriteLine($"[process] stopping host={Environment.ProcessId} child={Id} tree={entireProcessTree}");
			if (_managed is { } managed) {
				managed.Kill(entireProcessTree);
			} else {
				using var process = Process.GetProcessById(Id);
				process.Kill(entireProcessTree);
			}
			Console.WriteLine($"[process] stop sent host={Environment.ProcessId} child={Id}");
		}
	}

	/// <summary>Drains the child's two output streams as lines.</summary>
	public Task DrainLinesAsync(Action<string> output, Action<string> error) =>
		Task.WhenAll(DrainAsync(StandardOutput, output), DrainAsync(StandardError, error));

	private static async Task DrainAsync(StreamReader reader, Action<string> receive) {
		while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) receive(line);
	}

	private async Task ObserveManagedExitAsync(Process process) {
		try {
			await process.WaitForExitAsync().ConfigureAwait(false);
			_temp.Release();
			lock (_gate) _exit.TrySetResult(process.ExitCode);
		} catch (Exception ex) {
			_exit.TrySetException(ex);
		}
	}

	/// <summary>Releases the child's local streams and process handle; the supervisor owns stopping it.</summary>
	public void Dispose() {
		try {
			StandardInput.Dispose();
		} catch (IOException ex) {
			Console.WriteLine($"[process] closed stdin host={Environment.ProcessId} child={Id}: {ex.Message}");
		}
		StandardOutput.Dispose();
		StandardError.Dispose();
		if (_managed is { } process) {
			_ = _exit.Task.ContinueWith(_ => process.Dispose(), CancellationToken.None,
				TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
		}
	}
}
