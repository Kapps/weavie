using System.Collections.Concurrent;

namespace Weavie.Hosting;

/// <summary>
/// Resolves and caches one value per key for the cache's lifetime: concurrent callers for the same key share
/// the in-flight resolve, and a canceled resolve is never cached, so the next caller retries instead of being
/// stuck replaying a canceled result forever.
/// </summary>
internal sealed class KeyedResolveOnceCache<TKey, TValue> where TKey : notnull {
	private readonly ConcurrentDictionary<TKey, Task<TValue>> _cache = new();

	public Task<TValue> GetOrResolveAsync(TKey key, Func<TKey, Task<TValue>> resolve) {
		while (true) {
			if (_cache.TryGetValue(key, out var cached)) {
				if (!cached.IsCanceled) {
					return cached;
				}

				_cache.TryRemove(new KeyValuePair<TKey, Task<TValue>>(key, cached));
				continue;
			}

			var resolved = resolve(key);
			if (_cache.TryAdd(key, resolved)) {
				return resolved;
			}
		}
	}
}
