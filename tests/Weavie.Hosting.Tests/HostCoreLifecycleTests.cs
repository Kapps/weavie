using Xunit;

namespace Weavie.Hosting.Tests;

// 2026-07-19 16:05 UTC: main CI (linux) failed 141 tests with
// System.IO.FileNotFoundException loading Microsoft.AspNetCore.Http from WebApplicationBuilder.CreateBuilder(),
// and a prior run (2026-07-16 05:11 UTC) failed a different TestHost-backed test with
// System.OutOfMemoryException on Thread.StartCore() - both cascading-resource-exhaustion signatures.
// Run: https://github.com/Kapps/weavie/actions/runs/29694172917
// Root cause: every other class that boots a real TestHost/HostCore is serialized via
// [Collection(TestCollections.HostIntegration)], but this class was missing that attribute, so its real
// Kestrel-backed HostCore instances ran concurrently with the "serialized" collection, over-subscribing the
// 2-core CI runner. Added the same collection here to close the gap.
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
