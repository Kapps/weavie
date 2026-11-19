using System.Collections.Concurrent;
using Weavie.Core.Configuration;

namespace Weavie.Hosting.Messaging;

/// <summary>The home each connected page named in its hello; a page that named none is served by this host.</summary>
internal sealed class PageHomes {
	private readonly ConcurrentDictionary<WebPeer, DeviceIdentity> _homes = new();

	public void Named(WebPeer page, DeviceIdentity? home) {
		if (home is null) {
			_homes.TryRemove(page, out _);
		} else {
			_homes[page] = home;
		}
	}

	public void Disconnected(WebPeer page) => _homes.TryRemove(page, out _);

	public DeviceIdentity? Of(WebPeer page) => _homes.GetValueOrDefault(page);

	/// <summary>A connected page served by <paramref name="home"/>: <paramref name="preferred"/> when it still is one.</summary>
	public WebPeer? PageFrom(DeviceIdentity home, WebPeer? preferred) {
		if (preferred is { } page && _homes.TryGetValue(page, out var named) && named.Id == home.Id) {
			return page;
		}

		foreach (var (candidate, candidateHome) in _homes) {
			if (candidateHome.Id == home.Id) {
				return candidate;
			}
		}

		return null;
	}
}
