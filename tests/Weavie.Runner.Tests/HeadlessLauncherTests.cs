using Xunit;

namespace Weavie.Runner.Tests;

// The worker's port is picked by a bind-then-release probe (BackendManager.AllocatePort) that can race another
// fresh loopback allocation and collide — see the crash log this guards against: a worker that fails to bind
// keeps retrying the exact same doomed port on every restart until the crash-loop breaker gives up. A restart
// whose previous attempt logged that exact conflict must reroll an unpinned port instead of repeating it — but
// any OTHER crash (the worker had actually bound and was serving tabs, then died for an unrelated reason) must
// come back on the same port, or every open tab's fixed-URL WebSocket reconnect loop is stranded on a dead port.
public sealed class HeadlessLauncherTests {
	[Fact]
	public async Task RestartRerollsAnUnpinnedPortAfterABindConflict() {
		string script = WriteCrashScript("echo 'System.Net.Sockets.AddressInUseException: nope'");
		try {
			var backend = new WorkspaceBackend { WorkspaceRoot = Path.GetTempPath(), Port = 1111, Token = "t" };
			var rerolled = new Queue<int>([2222, 3333]);
			var launcher = new HeadlessLauncher(() => script, "127.0.0.1", log: null);

			var supervisor = launcher.BuildSupervisor(backend, () => rerolled.Dequeue());
			supervisor.Start();
			try {
				await WaitUntilAsync(() => supervisor.RestartCount >= 1);
				Assert.Equal(2222, backend.Port);

				await WaitUntilAsync(() => supervisor.RestartCount >= 2);
				Assert.Equal(3333, backend.Port);
			} finally {
				supervisor.Dispose();
			}
		} finally {
			File.Delete(script);
		}
	}

	[Fact]
	public async Task RestartKeepsTheSamePortAfterAnUnrelatedCrash() {
		string script = WriteCrashScript("echo 'boom: an unrelated failure'");
		try {
			var backend = new WorkspaceBackend { WorkspaceRoot = Path.GetTempPath(), Port = 4444, Token = "t" };
			bool reallocateCalled = false;
			var launcher = new HeadlessLauncher(() => script, "127.0.0.1", log: null);

			var supervisor = launcher.BuildSupervisor(backend, () => {
				reallocateCalled = true;
				return 9999;
			});
			supervisor.Start();
			try {
				await WaitUntilAsync(() => supervisor.RestartCount >= 2);
				Assert.Equal(4444, backend.Port);
				Assert.False(reallocateCalled);
			} finally {
				supervisor.Dispose();
			}
		} finally {
			File.Delete(script);
		}
	}

	[Fact]
	public async Task RestartNeverReallocatesAPinnedPortEvenAfterABindConflict() {
		string script = WriteCrashScript("echo 'System.Net.Sockets.AddressInUseException: nope'");
		try {
			var backend = new WorkspaceBackend { WorkspaceRoot = Path.GetTempPath(), Port = 7001, Token = "t" };
			var launcher = new HeadlessLauncher(() => script, "127.0.0.1", log: null);

			var supervisor = launcher.BuildSupervisor(backend, reallocatePort: null);
			supervisor.Start();
			try {
				await WaitUntilAsync(() => supervisor.RestartCount >= 2);
				Assert.Equal(7001, backend.Port);
			} finally {
				supervisor.Dispose();
			}
		} finally {
			File.Delete(script);
		}
	}

	// A tiny executable that prints `body` to stdout then exits 1 — a stand-in worker whose crash carries (or
	// doesn't carry) the bind-conflict signature `HeadlessLauncher` watches for. POSIX-only, matching this test
	// project's Linux-only slot in the CI matrix ("Test (remote / runner / headless)").
	private static string WriteCrashScript(string body) {
		if (OperatingSystem.IsWindows()) {
			throw new PlatformNotSupportedException("HeadlessLauncherTests spawns a POSIX shell script.");
		}

		string path = Path.Combine(Path.GetTempPath(), $"weavie-runner-test-{Guid.NewGuid():N}.sh");
		File.WriteAllText(path, $"#!/bin/sh\n{body}\nexit 1\n");
		File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		return path;
	}

	private static async Task WaitUntilAsync(Func<bool> condition) {
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
		while (!condition()) {
			if (DateTime.UtcNow > deadline) {
				throw new TimeoutException("condition was not met in time");
			}

			await Task.Delay(20);
		}
	}
}
