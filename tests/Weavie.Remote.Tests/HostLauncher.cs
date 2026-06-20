using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Weavie.Remote.Tests;

/// <summary>
/// Locates the built host dlls and launches them as real processes for black-box auth probing. These tests
/// assert against exactly what ships (the full Kestrel pipeline + the central auth gate + the fail-closed
/// startup), so no internal seam can pass while the wiring is bypassable.
/// </summary>
internal static class Hosts {
	public static string RepoRoot { get; } = FindRepoRoot();

	// The hosts build in the same configuration as this test assembly; derive it from our own output path.
	public static string Config { get; } =
		AppContext.BaseDirectory.Replace('\\', '/').Contains("/Release/", StringComparison.Ordinal) ? "Release" : "Debug";

	public static string HeadlessDll => Dll("Weavie.Headless");

	public static string RunnerDll => Dll("Weavie.Runner");

	/// <summary>Grabs a free loopback TCP port (bind 0, release, reuse). Racy but fine for tests.</summary>
	public static int FreePort() {
		var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
		listener.Start();
		try {
			return ((IPEndPoint)listener.LocalEndpoint).Port;
		} finally {
			listener.Stop();
		}
	}

	private static string Dll(string project) =>
		Path.Combine(RepoRoot, "src", project, "bin", Config, "net10.0", $"{project}.dll");

	private static string FindRepoRoot() {
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "weavie.slnx"))) {
			dir = dir.Parent;
		}

		return dir?.FullName ?? throw new InvalidOperationException("could not locate weavie.slnx above the test output.");
	}
}

/// <summary>
/// A launched host process. <see cref="StartAsync"/> waits for a readiness marker (and fails if the process
/// exits first); <see cref="RunToExitAsync"/> runs a process expected to refuse and exit. Disposing kills the
/// whole tree (so a runner's spawned worker can't linger).
/// </summary>
public sealed class HostHandle : IAsyncDisposable {
	private readonly Process _process;

	private HostHandle(Process process, int port) {
		_process = process;
		Port = port;
	}

	public int Port { get; }

	public string BaseUrl => $"http://127.0.0.1:{Port}";

	public static async Task<HostHandle> StartAsync(
		string dll, IReadOnlyList<string> args, int port, string readyMarker, TimeSpan timeout) {
		var output = new StringBuilder();
		var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		var psi = new ProcessStartInfo("dotnet") {
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		psi.ArgumentList.Add(dll);
		foreach (string arg in args) {
			psi.ArgumentList.Add(arg);
		}

		var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
		void OnData(object? _, DataReceivedEventArgs e) {
			if (e.Data is null) {
				return;
			}

			lock (output) {
				output.AppendLine(e.Data);
			}

			if (e.Data.Contains(readyMarker, StringComparison.Ordinal)) {
				ready.TrySetResult();
			}
		}

		process.OutputDataReceived += OnData;
		process.ErrorDataReceived += OnData;
		process.Exited += (_, _) => ready.TrySetException(
			new InvalidOperationException($"host exited (code {SafeExit(process)}) before ready:\n{Snapshot(output)}"));

		process.Start();
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		try {
			await ready.Task.WaitAsync(timeout).ConfigureAwait(false);
			// The hosts print the banner just BEFORE app.RunAsync() actually binds the port, so the marker alone
			// races the listener. Poll an actual TCP connect until it accepts, closing the race deterministically.
			await WaitForPortAsync(port, process, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
		} catch {
			TreeKill(process);
			throw new InvalidOperationException($"host did not become ready in {timeout.TotalSeconds:F0}s:\n{Snapshot(output)}");
		}

		return new HostHandle(process, port);
	}

	private static async Task WaitForPortAsync(int port, Process process, TimeSpan timeout) {
		var deadline = DateTime.UtcNow + timeout;
		while (DateTime.UtcNow < deadline) {
			if (process.HasExited) {
				throw new InvalidOperationException($"host exited (code {SafeExit(process)}) before its port opened.");
			}

			try {
				using var client = new TcpClient();
				await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
				return;
			} catch (Exception ex) when (ex is SocketException or TimeoutException) {
				await Task.Delay(100).ConfigureAwait(false);
			}
		}

		throw new TimeoutException($"port {port} did not start accepting connections in {timeout.TotalSeconds:F0}s.");
	}

	/// <summary>Runs a host expected to refuse + exit; returns its exit code and combined output.</summary>
	public static async Task<(int ExitCode, string Output)> RunToExitAsync(
		string dll, IReadOnlyList<string> args, TimeSpan timeout) {
		var output = new StringBuilder();
		var psi = new ProcessStartInfo("dotnet") {
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		psi.ArgumentList.Add(dll);
		foreach (string arg in args) {
			psi.ArgumentList.Add(arg);
		}

		using var process = new Process { StartInfo = psi };
		process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
		process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
		process.Start();
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		try {
			await process.WaitForExitAsync(new CancellationTokenSource(timeout).Token).ConfigureAwait(false);
		} catch (OperationCanceledException) {
			TreeKill(process);
			throw new InvalidOperationException($"host did not exit in {timeout.TotalSeconds:F0}s (expected refusal):\n{Snapshot(output)}");
		}

		return (process.ExitCode, Snapshot(output));
	}

	public ValueTask DisposeAsync() {
		TreeKill(_process);
		_process.Dispose();
		return ValueTask.CompletedTask;
	}

	private static string Snapshot(StringBuilder output) {
		lock (output) {
			return output.ToString();
		}
	}

	private static int SafeExit(Process process) {
		try {
			return process.ExitCode;
		} catch (InvalidOperationException) {
			return -1;
		}
	}

	private static void TreeKill(Process process) {
		try {
			if (!process.HasExited) {
				process.Kill(entireProcessTree: true);
			}
		} catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) {
		}
	}
}
