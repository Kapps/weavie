using Weavie.Core.Configuration;

namespace Weavie.Hosting;

/// <summary>
/// The device a session belongs to: the home of the page that last gave it input, and that page. A session with
/// no home belongs to its own host. See <c>docs/specs/settings-ownership.md</c>.
/// </summary>
public sealed class SessionDevice {
	private readonly Lock _gate = new();
	private DeviceIdentity? _home;
	private WebPeer? _page;

	/// <summary>Raised when the session moves to a device with another home.</summary>
	public event Action? Moved;

	/// <summary>The device's home and the page that claimed it; no home is this host, no page is a restored or inherited claim.</summary>
	public (DeviceIdentity? Home, WebPeer? Page) Current {
		get {
			lock (_gate) {
				return (_home, _page);
			}
		}
	}

	/// <summary>Sets the device a restored or newly created session starts on.</summary>
	public void Restore(DeviceIdentity? home) {
		lock (_gate) {
			_home = home;
		}
	}

	/// <summary>Makes <paramref name="page"/>, served by <paramref name="home"/>, the session's device.</summary>
	public void Claim(DeviceIdentity? home, WebPeer page) {
		bool moved;
		lock (_gate) {
			moved = _home != home;
			_home = home;
			_page = page;
		}

		if (moved) {
			Moved?.Invoke();
		}
	}
}
