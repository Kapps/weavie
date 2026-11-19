using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

// Device settings: this host answers for the pages it serves, and relays a session's device requests to a page
// from that device's home. See docs/specs/settings-ownership.md.
public sealed partial class HostCore {
	private void WireDeviceMessages() =>
		_messages.Host.Feature("device").Handle(
			"settings", MessageActivity.ActionFailed("Changing a device setting"), WireJson.Default.SettingsRequest, WireJson.Default.SettingsReply,
			(request, _) => Task.FromResult(_settingsTools.Device(request)));

	private SessionDevice NewDevice(DeviceIdentity? home) {
		var device = new SessionDevice();
		device.Restore(home);
		device.Moved += () => _ui.Post(PersistSessionState);
		return device;
	}

	// Input a page gives a session makes that page's home the session's device.
	private void TrackDevice(HostSession session, SessionDevice device) =>
		session.Bus.Received += (page, envelope) => {
			if (IsUserInput(envelope)) {
				device.Claim(_messages.Homes.Of(page), page);
			}
		};

	private static bool IsUserInput(MessageEnvelope envelope) => envelope.Feature switch {
		"agent" => envelope.Name is "typing" or "submit" or "replyAside" or "input" or "permission",
		"commands" => envelope.Name == "invoke",
		_ => envelope.Name == "input"
			&& envelope.Payload.ValueKind == JsonValueKind.Object
			&& envelope.Payload.TryGetProperty("userInitiated", out var userInitiated)
			&& userInitiated.ValueKind == JsonValueKind.True,
	};

	private sealed class BoundDeviceSettings(HostCore core, SessionDevice device) : IDeviceSettings {
		public async Task<SettingsReply> RequestAsync(SettingsRequest request, CancellationToken ct) {
			var (home, claimant) = device.Current;
			if (home is null) {
				return core._settingsTools.Device(request);
			}

			if (core._messages.Homes.PageFrom(home, claimant) is not { } page) {
				throw new DeviceUnavailableException(
					$"{home.Name} isn't connected to {core._settingsTools.HostName}, so its device settings can't be read or changed from this session.");
			}

			try {
				return await core._messages.Host.RequestAsync(
					page, "device", "settings", WireJson.Default.SettingsRequest, WireJson.Default.SettingsReply, request, ct).ConfigureAwait(false);
			} catch (InvalidOperationException) when (core._messages.Homes.Of(page) is null) {
				throw new DeviceUnavailableException($"{home.Name} disconnected before it answered; the setting may or may not have changed.");
			}
		}
	}
}
