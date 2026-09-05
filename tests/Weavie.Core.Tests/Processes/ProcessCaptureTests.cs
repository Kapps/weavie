using Weavie.Core.Processes;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// The pipe discipline <see cref="ProcessCapture"/> exists to own: both streams drained concurrently well past the
/// OS pipe buffer, a start failure reported rather than thrown, and a canceled run killed with its output kept.
/// Every child runs through the platform shell out of a temp working directory, so no command needs quoting.
/// </summary>
public sealed class ProcessCaptureTests : IDisposable {
	// Comfortably past a pipe buffer on every platform (64 KiB on Linux, 4-8 KiB typical on Windows): a child
	// writing this much blocks forever on the write unless its reader is already draining.
	private const int FloodBytes = 512 * 1024;

	private readonly string _directory = Directory.CreateTempSubdirectory("weavie-capture-").FullName;

	public void Dispose() => Directory.Delete(_directory, recursive: true);

	[Fact]
	public async Task DrainsBothStreamsPastThePipeBuffer() {
		await File.WriteAllTextAsync(Path.Combine(_directory, "flood.txt"), new string('w', FloodBytes));
		string emit = Pick(posix: "cat flood.txt", windows: "type flood.txt");

		// The deadlock this type exists to prevent: a chatty child fills a pipe nobody is reading and blocks on the
		// write forever, so only a bounded wait ever ends the call. Reading one stream at a time deadlocks too.
		var result = await Run(CancellationToken.None, emit, $"{emit} 1>&2").WaitAsync(TimeSpan.FromSeconds(60));

		Assert.Null(result.StartFailure);
		Assert.Equal(0, result.ExitCode);
		Assert.Equal(FloodBytes, result.StdOut.Count(character => character == 'w'));
		Assert.Equal(FloodBytes, result.StdErr.Count(character => character == 'w'));
	}

	[Fact]
	public async Task ReportsAMissingExecutableInsteadOfThrowing() {
		var result = await ProcessCapture.RunAsync(
			new ProcessCaptureRequest { FileName = "weavie-no-such-tool", Arguments = [] }, CancellationToken.None);

		Assert.NotNull(result.StartFailure);
		Assert.Equal(-1, result.ExitCode);
		Assert.Equal(string.Empty, result.StdOut);
		Assert.Equal(string.Empty, result.StdErr);
	}

	[Fact]
	public async Task ReportsTheExitCodeAndKeepsTheStreamsApart() {
		var result = await Run(CancellationToken.None, "echo out", "echo problem 1>&2", "exit 3");

		Assert.Null(result.StartFailure);
		Assert.Equal(3, result.ExitCode);
		Assert.Contains("out", result.StdOut, StringComparison.Ordinal);
		Assert.DoesNotContain("problem", result.StdOut, StringComparison.Ordinal);
		Assert.Contains("problem", result.StdErr, StringComparison.Ordinal);
	}

	[Fact]
	public async Task WritesStandardInputVerbatimAndClosesIt() {
		var result = await ProcessCapture.RunAsync(
			new ProcessCaptureRequest { FileName = "sort", Arguments = [], StandardInput = "beta\nalpha\n" },
			CancellationToken.None);

		Assert.Null(result.StartFailure);
		Assert.Equal(0, result.ExitCode);
		// Exact: an encoding preamble ahead of the child's first line would corrupt it and show up right here.
		Assert.Equal("alpha beta", result.StdOut.ReplaceLineEndings(" ").Trim());
		Assert.DoesNotContain("\uFEFF", result.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AppliesTheWorkingDirectoryAndEnvironment() {
		var result = await ProcessCapture.RunAsync(
			new ProcessCaptureRequest {
				FileName = Pick(posix: "/bin/sh", windows: "cmd.exe"),
				Arguments = [
					Pick(posix: "-c", windows: "/c"),
					Pick(posix: "printf %s $WEAVIE_PROBE; pwd", windows: "echo %WEAVIE_PROBE%& cd"),
				],
				WorkingDirectory = _directory,
				Environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["WEAVIE_PROBE"] = "seen" },
			},
			CancellationToken.None);

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("seen", result.StdOut, StringComparison.Ordinal);
		Assert.Contains(Path.GetFileName(_directory), result.StdOut, StringComparison.Ordinal);
	}

	[Fact]
	public async Task CancellationKillsTheTreeAndKeepsWhatTheChildPrinted() {
		using var cancellation = new CancellationTokenSource();
		var run = Run(
			cancellation.Token,
			"echo started",
			Pick(posix: "touch ready", windows: "type nul > ready"),
			Pick(posix: "sleep 300", windows: "ping -n 300 127.0.0.1 > nul"));
		await WaitForFileAsync(Path.Combine(_directory, "ready"));

		await cancellation.CancelAsync();
		var thrown = await Assert.ThrowsAsync<ProcessCanceledException>(() => run).WaitAsync(TimeSpan.FromSeconds(60));

		Assert.Contains("started", thrown.StdOut, StringComparison.Ordinal);
	}

	private static async Task WaitForFileAsync(string path) {
		var deadline = DateTime.UtcNow.AddSeconds(60);
		while (!File.Exists(path)) {
			Assert.True(DateTime.UtcNow < deadline, $"The child never created {path}.");
			await Task.Delay(20);
		}
	}

	private Task<ProcessCaptureResult> Run(CancellationToken ct, params string[] commands) =>
		ProcessCapture.RunAsync(
			new ProcessCaptureRequest {
				FileName = Pick(posix: "/bin/sh", windows: "cmd.exe"),
				Arguments = [Pick(posix: "-c", windows: "/c"), string.Join(Pick(posix: "; ", windows: " & "), commands)],
				WorkingDirectory = _directory,
			},
			ct);

	private static string Pick(string posix, string windows) => OperatingSystem.IsWindows() ? windows : posix;
}
