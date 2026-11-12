using System.Runtime.InteropServices;
using System.Text;
using Weavie.Core.Terminal;
using Xunit;
using static Weavie.Core.Terminal.NativeMethods;

namespace Weavie.Core.Tests;

public sealed class PosixPtyTerminalTests {
	private static (string Output, int ExitCode) RunToCompletion(TerminalStartInfo info, int timeoutSeconds) {
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

	private static int StopOnceReady(TerminalStartInfo info) {
		using var terminal = new PosixPtyTerminal();
		using var ready = new ManualResetEventSlim();
		int exitCode = -1;
		terminal.Output += data => {
			if (Encoding.UTF8.GetString(data).Contains("ready", StringComparison.Ordinal)) {
				ready.Set();
			}
		};
		terminal.Exited += code => exitCode = code;
		terminal.Start(info);
		Assert.True(ready.Wait(TimeSpan.FromSeconds(10)), "child never reported ready");
		terminal.Dispose();
		return exitCode;
	}

	[Fact]
	public void InitialSizeIsAvailableToTheChildAtLaunch() {
		if (OperatingSystem.IsWindows()) return;

		var (output, exitCode) = RunToCompletion(new TerminalStartInfo {
			Command = "/bin/stty",
			Arguments = ["size"],
			Columns = 131,
			Rows = 37,
		}, 5);
		Assert.Equal(0, exitCode);
		Assert.Contains("37 131", output, StringComparison.Ordinal);
	}

	[Fact]
	public void Dispose_HangsUpAnInteractiveShell_InsteadOfKillingIt() {
		if (OperatingSystem.IsWindows()) return;

		int exitCode = StopOnceReady(InteractiveShell());

		Assert.Equal(129, exitCode);
	}

	[Fact]
	public void Start_ClearsTheLaunchingThreadsBlockedHangupSignal() {
		if (!OperatingSystem.IsLinux()) return;

		Assert.Equal(0, sigemptyset(out var blocked));
		Assert.Equal(0, sigaddset(ref blocked, SIGHUP));
		Assert.Equal(0, pthread_sigmask(0, ref blocked, out var previous));
		try {
			Assert.Equal(129, StopOnceReady(InteractiveShell()));
		} finally {
			Assert.Equal(0, pthread_sigmask(2, ref previous, out _));
		}
	}

	private static TerminalStartInfo InteractiveShell() => new() {
		Command = "/bin/bash",
		Arguments = ["--noprofile", "--norc", "-i"],
		Environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["PS1"] = "ready> " },
		HangUpOnStop = true,
	};

	[DllImport("libc")]
	private static extern int sigaddset(ref LinuxSignalSet set, int signal);

	[DllImport("libc")]
	private static extern int pthread_sigmask(int how, ref LinuxSignalSet set, out LinuxSignalSet previous);

	[Fact]
	public void Dispose_DoesNotHangUpAChildThatStopsOnSigterm() {
		if (OperatingSystem.IsWindows()) return;

		int exitCode = StopOnceReady(new TerminalStartInfo {
			Command = "/bin/sh",
			Arguments = ["-c", "trap 'exit 7' TERM; trap 'exit 9' HUP; echo ready; while :; do sleep 0.05; done"],
		});

		Assert.Equal(7, exitCode);
	}

	[Fact]
	public void Dispose_StopsOnlyTheOwnedTerminal_AndAllowsReplacement() {
		if (OperatingSystem.IsWindows()) return;

		using var survivor = new PosixPtyTerminal();
		survivor.Start(new TerminalStartInfo { Command = "/bin/sleep", Arguments = ["60"] });
		for (int i = 0; i < 8; i++) {
			using var terminal = new PosixPtyTerminal();
			using var exited = new ManualResetEventSlim();
			terminal.Exited += _ => exited.Set();
			terminal.Start(new TerminalStartInfo { Command = "/bin/sleep", Arguments = ["60"] });
			terminal.Dispose();
			Assert.True(exited.IsSet, "Dispose returned before the child was reaped");
			Assert.False(terminal.IsRunning);
			Assert.True(survivor.IsRunning, "Stopping one terminal terminated its sibling");
		}
	}

	[Fact]
	public void FailedLaunch_ReportsTheActualErrorSynchronously() {
		if (OperatingSystem.IsWindows()) return;

		using var terminal = new PosixPtyTerminal();
		var error = Assert.Throws<IOException>(() => terminal.Start(new TerminalStartInfo {
			Command = "/weavie-missing-executable",
		}));
		Assert.Contains(OperatingSystem.IsMacOS() ? "errno 2" : "code 2", error.Message, StringComparison.Ordinal);
		Assert.False(terminal.IsRunning);
	}

	[Fact]
	public void Spawn_Echo_ProducesOutputAndExitsZero() {
		if (OperatingSystem.IsWindows()) {
			return;
		}

		var (output, exitCode) = RunToCompletion(new TerminalStartInfo {
			Command = "/bin/echo",
			Arguments = ["hello weavie"],
		}, 5);

		Assert.Contains("hello weavie", output, StringComparison.Ordinal);
		Assert.Equal(0, exitCode);
	}

	[Fact]
	public void InjectedEnvironment_IsVisibleToChild() {
		if (OperatingSystem.IsWindows()) {
			return;
		}

		var (output, _) = RunToCompletion(new TerminalStartInfo {
			Command = "/bin/sh",
			Arguments = ["-c", "printf '[%s]' \"$WEAVIE_MARKER\""],
			Environment = new Dictionary<string, string> { ["WEAVIE_MARKER"] = "marker123" },
		}, 5);

		Assert.Contains("[marker123]", output, StringComparison.Ordinal);
	}

	[Fact]
	public void WorkingDirectory_IsHonored() {
		if (OperatingSystem.IsWindows()) {
			return;
		}

		var (output, _) = RunToCompletion(new TerminalStartInfo {
			Command = "/bin/sh",
			Arguments = ["-c", "pwd"],
			WorkingDirectory = "/tmp",
		}, 5);

		Assert.Contains("/tmp", output, StringComparison.Ordinal);
	}

	[Fact]
	public void RemovedEnvironment_IsHiddenFromChild() {
		if (OperatingSystem.IsWindows()) {
			return;
		}

		Environment.SetEnvironmentVariable("WEAVIE_REMOVE_ME", "should-be-gone");
		try {
			var (output, _) = RunToCompletion(new TerminalStartInfo {
				Command = "/bin/sh",
				Arguments = ["-c", "printf '[%s]' \"$WEAVIE_REMOVE_ME\""],
				RemoveEnvironment = ["WEAVIE_REMOVE_ME"],
			}, 5);

			Assert.Contains("[]", output, StringComparison.Ordinal);
		} finally {
			Environment.SetEnvironmentVariable("WEAVIE_REMOVE_ME", null);
		}
	}
}
