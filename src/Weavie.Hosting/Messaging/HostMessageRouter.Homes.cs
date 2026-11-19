using Weavie.Core.Configuration;

namespace Weavie.Hosting.Messaging;

/// <summary>The home a page named in its hello: no device for a page this host serves.</summary>
internal sealed record PageHome(DeviceIdentity? Device);

// Which home each connected page is served by, read from the host bus's own peers so a page that left has none.
internal sealed partial class HostMessageRouter {
	/// <summary>The home of <paramref name="page"/>, once it has said hello and while it stays connected.</summary>
	internal bool TryGetHome(WebPeer page, out DeviceIdentity? home) {
		var named = Host.TryGetPeer(page, out var peer) ? peer.Home : null;
		home = named?.Device;
		return named is not null;
	}

	/// <summary>A connected page served by <paramref name="home"/>: <paramref name="preferred"/> when it still is one.</summary>
	internal WebPeer? PageFrom(DeviceIdentity home, WebPeer? preferred) {
		if (preferred is { } page && TryGetHome(page, out var named) && named?.Id == home.Id) {
			return page;
		}

		return Host.Peers.FirstOrDefault(peer => peer.Home?.Device?.Id == home.Id)?.Transport;
	}
}
