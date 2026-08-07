using Weavie.Core.Commands;
using Weavie.Core.Configuration;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private const string AutomaticInferenceOfferKey = "inference-automatic-opt-in";
	private int _automaticInferenceOffered;

	private void OfferAutomaticInference() {
		if (AutomaticInferenceEnabled()
			|| Interlocked.Exchange(ref _automaticInferenceOffered, 1) != 0) {
			return;
		}

		Notify(
			"info",
			"Let Weavie use automatic inference for small suggestions, such as repository-aware branch names. This may use tokens.",
			AutomaticInferenceOfferKey,
			"Allow",
			CoreCommands.EnableAutomaticInference,
			null);
	}

	private bool AutomaticInferenceEnabled() =>
		_settings.RequireBool(InferenceSettings.Enabled)
		&& _settings.RequireBool(InferenceSettings.AllowAutomatic);

	private void ClearAutomaticInferenceOffer() => ClearNotify(AutomaticInferenceOfferKey);
}
