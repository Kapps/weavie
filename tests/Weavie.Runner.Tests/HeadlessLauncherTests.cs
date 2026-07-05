using Xunit;

namespace Weavie.Runner.Tests;

// The worker's port is picked by a bind-then-release probe (BackendManager.AllocatePort) that can race another
// fresh loopback allocation and collide — see the crash log this guards against: a worker that fails to bind
// keeps retrying the exact same doomed port on every restart until the crash-loop breaker gives up. A restart
// past the first must reroll an unpinned port instead of repeating it.
public sealed class HeadlessLauncherTests {
	[Fact]
	public async Task RestartRerollsAnUnpinnedPort() {
		var backend = new WorkspaceBackend { WorkspaceRoot = Path.GetTempPath(), Port = 1111, Token = "t" };
		var rerolled = new Queue<int>([2222, 3333]);
		var launcher = new HeadlessLauncher(() => "/bin/false", "127.0.0.1", log: null);

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
	}

	[Fact]
	public async Task RestartNeverReallocatesAPinnedPort() {
		var backend = new WorkspaceBackend { WorkspaceRoot = Path.GetTempPath(), Port = 7001, Token = "t" };
		var launcher = new HeadlessLauncher(() => "/bin/false", "127.0.0.1", log: null);

		var supervisor = launcher.BuildSupervisor(backend, reallocatePort: null);
		supervisor.Start();
		try {
			await WaitUntilAsync(() => supervisor.RestartCount >= 2);
			Assert.Equal(7001, backend.Port);
		} finally {
			supervisor.Dispose();
		}
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
