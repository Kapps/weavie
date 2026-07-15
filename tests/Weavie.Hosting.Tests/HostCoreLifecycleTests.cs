using Xunit;

namespace Weavie.Hosting.Tests;

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
