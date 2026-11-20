using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Inference;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private const string AutomaticInferenceOfferKey = "inference-automatic-opt-in";
	private int _automaticInferenceOffered;

	// A page from another device is offered this machine's inference on its own, so a page this host serves that is
	// still in setup isn't; this host's own pages are offered it together.
	private void OfferAutomaticInference(MessagePeer? visitor) {
		if (AutomaticInferenceEnabled()
			|| Interlocked.Exchange(ref _automaticInferenceOffered, 1) != 0) {
			return;
		}

		var offer = new ToastMessage(
			"info",
			"Let Weavie use automatic inference for small suggestions, such as repository-aware branch names. "
				+ $"{InferenceModelDescription.Describe(_settings, _agentProviders)} This may use tokens.",
			AutomaticInferenceOfferKey,
			new ToastAction("Allow", CoreCommands.EnableAutomaticInference, null));
		var notifications = _messages.Host.Feature("notifications");
		if (visitor is null) {
			notifications.Publish("show", WireJson.Default.ToastMessage, offer);
		} else {
			notifications.Target(visitor).Publish("show", WireJson.Default.ToastMessage, offer);
		}
	}

	private bool AutomaticInferenceEnabled() =>
		_settings.RequireBool(InferenceSettings.Enabled)
		&& _settings.RequireBool(InferenceSettings.AllowAutomatic);

	private void ClearAutomaticInferenceOffer() => ClearNotify(AutomaticInferenceOfferKey);
}
