using Xunit;

namespace Weavie.Hosting.Tests;

// Spins up real HostCore instances (some via TestHost.CreateUnstarted, deliberately unstarted for the race
// below) — serialize with the other HostIntegration tests so concurrent Kestrel/thread startup doesn't
// contend with them for OS threads/file descriptors under CI load. Flaked 2026-07-19 19:32 UTC missing this:
// an OutOfMemoryException starting a Kestrel thread here, plus an unrelated "too many open files" in
// CodexAppServerClientTests running concurrently. https://github.com/Kapps/weavie/actions/runs/29700647158/job/88229019983
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreLifecycleTests {
	[Fact]
	public async Task DisposeJoinsStartupBeforeStoppingTheHost() {
		await using var host = TestHost.CreateUnstarted();

		var start = host.Core.StartAsync();
		await host.Core.DisposeAsync();
		await start;

		Assert.False(host.Bridge.HasMessageReceiver);
		await Assert.ThrowsAsync<ObjectDisposedException>(() => host.Core.StartAsync());
	}

	[Fact]
	public async Task DisposeIsIdempotentAndDetachesTheWebBridge() {
		await using var host = await TestHost.StartAsync();
		Assert.True(host.Bridge.HasMessageReceiver);

		var first = host.Core.DisposeAsync().AsTask();
		var second = host.Core.DisposeAsync().AsTask();
		Assert.Same(first, second);
		await first;

		Assert.False(host.Bridge.HasMessageReceiver);
		await Assert.ThrowsAsync<ObjectDisposedException>(() => host.Core.StartAsync());
	}
}
