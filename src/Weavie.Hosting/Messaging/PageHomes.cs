using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Weavie.Core.Configuration;

namespace Weavie.Hosting.Messaging;

/// <summary>The home each page named in its hello: <c>null</c> for a page this host serves. A page is unknown until its hello.</summary>
internal sealed class PageHomes {
	private readonly ConcurrentDictionary<WebPeer, DeviceIdentity?> _homes = new();

	public void Named(WebPeer page, DeviceIdentity? home) => _homes[page] = home;

	public void Disconnected(WebPeer page) => _homes.TryRemove(page, out _);

	/// <summary>The home of <paramref name="page"/>, once it has said hello.</summary>
	public bool TryGet(WebPeer page, out DeviceIdentity? home) => _homes.TryGetValue(page, out home);

	/// <summary>A connected page served by <paramref name="home"/>: <paramref name="preferred"/> when it still is one.</summary>
	public WebPeer? PageFrom(DeviceIdentity home, WebPeer? preferred) {
		if (preferred is { } page && _homes.TryGetValue(page, out var named) && named?.Id == home.Id) {
			return page;
		}

		foreach (var (candidate, candidateHome) in _homes) {
			if (candidateHome?.Id == home.Id) {
				return candidate;
			}
		}

		return null;
	}
}
