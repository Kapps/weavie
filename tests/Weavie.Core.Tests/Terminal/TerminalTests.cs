using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Weavie.Core.Terminal;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class FakeTerminalTests {
	[Fact]
	public void Start_SetsRunningAndRecordsStartInfo() {
		var term = new FakeTerminal();
		var info = new TerminalStartInfo { Command = "/bin/zsh", Columns = 100, Rows = 30 };
		term.Start(info);
		Assert.True(term.IsRunning);
		Assert.Same(info, term.LastStartInfo);
	}

	[Fact]
	public void Write_IsRecorded() {
		var term = new FakeTerminal();
		term.Write("ls\r"u8.ToArray());
		Assert.Equal("ls\r", term.WrittenText);
	}

	[Fact]
	public void EmitOutput_RaisesOutput() {
		var term = new FakeTerminal();
		string? received = null;
		term.Output += b => received = Encoding.UTF8.GetString(b);
		term.EmitOutput("hello");
		Assert.Equal("hello", received);
	}

	[Fact]
	public void EmitExit_StopsRunningAndRaisesExit() {
		var term = new FakeTerminal();
		term.Start(new TerminalStartInfo { Command = "/bin/zsh" });
		int code = -1;
		term.Exited += c => code = c;
		term.EmitExit(0);
		Assert.False(term.IsRunning);
		Assert.Equal(0, code);
	}
}

public sealed class WindowsConPtyTerminalTests {
	[Fact]
	public void Dispose_KillsTheAttachedProcessTree() {
		if (!OperatingSystem.IsWindows()) {
			return;
		}

		using var term = new WindowsConPtyTerminal();
		var output = new StringBuilder();
		object outputGate = new();
		term.Output += bytes => {
			lock (outputGate) {
				output.Append(Encoding.UTF8.GetString(bytes));
			}
		};
		term.Start(new TerminalStartInfo {
			Command = "powershell.exe",
			Arguments = [
				"-NoProfile",
				"-Command",
				"$child = Start-Process ping.exe -ArgumentList '-t','127.0.0.1' -PassThru; "
					+ "Write-Output \"CHILD:$($child.Id)\"; Wait-Process -Id $child.Id",
			],
		});

		int childPid = 0;
		Assert.True(SpinWait.SpinUntil(() => {
			lock (outputGate) {
				var match = Regex.Match(output.ToString(), @"CHILD:(\d+)");
				return match.Success && int.TryParse(match.Groups[1].Value, out childPid);
			}
		}, TimeSpan.FromSeconds(10)), "child process id was not reported");

		term.Dispose();

		Assert.True(SpinWait.SpinUntil(() => !ProcessIsAlive(childPid), TimeSpan.FromSeconds(5)),
			$"child process {childPid} survived terminal disposal");
	}

	private static bool ProcessIsAlive(int pid) {
		try {
			using var process = Process.GetProcessById(pid);
			return !process.HasExited;
		} catch (ArgumentException) {
			return false;
		}
	}
}

/// <summary>
/// Real-PTY integration tests (macOS): spawn trivial processes to exercise the native path.
/// No-op on non-macOS so the suite stays green anywhere.
/// </summary>
public sealed class PosixPtyTerminalTests {
	private static (string Output, int ExitCode) RunToCompletion(TerminalStartInfo info, int timeoutSeconds = 5) {
		using var term = new PosixPtyTerminal();
		var sb = new StringBuilder();
		object sync = new();
		var exited = new ManualResetEventSlim(false);
		int exitCode = -1;

		term.Output += bytes => {
			lock (sync) {
				sb.Append(Encoding.UTF8.GetString(bytes));
			}
		};
		term.Exited += code => {
			exitCode = code;
			exited.Set();
		};

		term.Start(info);
		Assert.True(exited.Wait(TimeSpan.FromSeconds(timeoutSeconds)), "child process did not exit in time");

		lock (sync) {
			return (sb.ToString(), exitCode);
		}
	}

	[Fact]
	public void Spawn_Echo_ProducesOutputAndExitsZero() {
		if (!OperatingSystem.IsMacOS()) {
			return;
		}

		var (output, exitCode) = RunToCompletion(new TerminalStartInfo {
			Command = "/bin/echo",
			Arguments = ["hello weavie"],
		});

		Assert.Contains("hello weavie", output, StringComparison.Ordinal);
		Assert.Equal(0, exitCode);
	}

	[Fact]
	public void InjectedEnvironment_IsVisibleToChild() {
		if (!OperatingSystem.IsMacOS()) {
			return;
		}

		var (output, _) = RunToCompletion(new TerminalStartInfo {
			Command = "/bin/sh",
			Arguments = ["-c", "printf '[%s]' \"$WEAVIE_MARKER\""],
			Environment = new Dictionary<string, string> { ["WEAVIE_MARKER"] = "marker123" },
		});

		Assert.Contains("[marker123]", output, StringComparison.Ordinal);
	}

	[Fact]
	public void WorkingDirectory_IsHonored() {
		if (!OperatingSystem.IsMacOS()) {
			return;
		}

		var (output, _) = RunToCompletion(new TerminalStartInfo {
			Command = "/bin/sh",
			Arguments = ["-c", "pwd"],
			WorkingDirectory = "/tmp",
		});

		Assert.Contains("/tmp", output, StringComparison.Ordinal);
	}

	[Fact]
	public void RemovedEnvironment_IsHiddenFromChild() {
		if (!OperatingSystem.IsMacOS()) {
			return;
		}

		Environment.SetEnvironmentVariable("WEAVIE_REMOVE_ME", "should-be-gone");
		try {
			var (output, _) = RunToCompletion(new TerminalStartInfo {
				Command = "/bin/sh",
				Arguments = ["-c", "printf '[%s]' \"$WEAVIE_REMOVE_ME\""],
				RemoveEnvironment = ["WEAVIE_REMOVE_ME"],
			});

			Assert.Contains("[]", output, StringComparison.Ordinal);
		} finally {
			Environment.SetEnvironmentVariable("WEAVIE_REMOVE_ME", null);
		}
	}
}
