using Weavie.Core.Tips;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private readonly StartupTip _startupTip = StartupTips.Pick(Random.Shared);
	private int _startupTipOffered;

	private void OfferStartupTip() {
		if (Interlocked.Exchange(ref _startupTipOffered, 1) != 0) {
			return;
		}

		_messages.Host.Feature("tips").Publish("show", WireJson.Default.StartupTip, _startupTip);
	}
}
