using Weavie.Core.Tips;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private readonly StartupTip _startupTip = StartupTips.Pick(Random.Shared);
	private int _startupTipOffered;

	private void OfferStartupTip() {
		if (Interlocked.Exchange(ref _startupTipOffered, 1) != 0) {
			return;
		}

		_messages.Host.Feature("tips").Publish("show", _startupTip);
	}
}
