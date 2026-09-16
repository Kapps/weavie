using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class KeyedResolveOnceCacheTests {
	[Fact]
	public async Task GetOrResolveAsync_ResolvesOnce_AcrossRepeatedCallsForTheSameKey() {
		var cache = new KeyedResolveOnceCache<string, int>();
		int resolveCalls = 0;
		Task<int> Resolve(string key) {
			Interlocked.Increment(ref resolveCalls);
			return Task.FromResult(42);
		}

		int[] concurrent = await Task.WhenAll(
			cache.GetOrResolveAsync("origin", Resolve),
			cache.GetOrResolveAsync("origin", Resolve));
		int later = await cache.GetOrResolveAsync("origin", Resolve);

		Assert.Equal(1, resolveCalls);
		Assert.All(concurrent, value => Assert.Equal(42, value));
		Assert.Equal(42, later);
	}

	[Fact]
	public async Task GetOrResolveAsync_ResolvesIndependently_PerKey() {
		var cache = new KeyedResolveOnceCache<string, string>();

		string origin = await cache.GetOrResolveAsync("origin", key => Task.FromResult($"resolved-{key}"));
		string upstream = await cache.GetOrResolveAsync("upstream", key => Task.FromResult($"resolved-{key}"));

		Assert.Equal("resolved-origin", origin);
		Assert.Equal("resolved-upstream", upstream);
	}

	[Fact]
	public async Task GetOrResolveAsync_DoesNotCacheACanceledResolve() {
		var cache = new KeyedResolveOnceCache<string, int>();
		int resolveCalls = 0;

		using (var cts = new CancellationTokenSource()) {
			cts.Cancel();
			var canceled = cache.GetOrResolveAsync("origin", async _ => {
				Interlocked.Increment(ref resolveCalls);
				await Task.Delay(Timeout.Infinite, cts.Token);
				return 1;
			});
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
		}

		int recovered = await cache.GetOrResolveAsync("origin", _ => {
			Interlocked.Increment(ref resolveCalls);
			return Task.FromResult(7);
		});

		Assert.Equal(2, resolveCalls);
		Assert.Equal(7, recovered);
	}
}
