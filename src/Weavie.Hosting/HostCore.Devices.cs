using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

// Device settings: this host answers for the pages it serves, and relays a session's device requests to a page
// from that device's home. See docs/specs/settings-ownership.md.
public sealed partial class HostCore {
	private void WireDeviceMessages() =>
		_messages.Host.Feature("device").Handle(
			"settings", MessageActivity.ActionFailed("Reaching device settings"), WireJson.Default.SettingsRequest, WireJson.Default.SettingsReply,
			(request, _) => Task.FromResult(_settingsTools.Device(request)));

	private SessionDevice NewDevice(DeviceIdentity? home) {
		var device = new SessionDevice();
		device.Restore(home);
		device.Moved += () => _ui.Post(PersistSessionState);
		return device;
	}

	// Input a page gives a session after its hello makes that page's home the session's device.
	private void TrackDevice(HostSession session, SessionDevice device) =>
		session.Bus.Received += (page, envelope) => {
			if (IsUserInput(envelope) && _messages.TryGetHome(page, out var home)) {
				device.Claim(home, page);
			}
		};

	private static bool IsUserInput(MessageEnvelope envelope) => envelope.Feature switch {
		"agent" => envelope.Name is "typing" or "submit" or "replyAside" or "input" or "permission" or "authenticate" or "interrupt",
		"commands" => envelope.Name == "invoke",
		"pullRequests" => envelope.Name == "open",
		_ => envelope.Feature.StartsWith("terminal.", StringComparison.Ordinal)
			&& envelope.Name == "input"
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

			if (core._messages.PageFrom(home, claimant) is not { } page) {
				throw new DeviceUnavailableException(
					$"{home.Name} isn't connected to {core._settingsTools.HostName}, so its device settings can't be read or changed from this session.");
			}

			using var timeout = new CancellationTokenSource(core._operationDeadline, core._time);
			using var either = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
			try {
				var answer = core._messages.Host.RequestAsync(
					page, "device", "settings", WireJson.Default.SettingsRequest, WireJson.Default.SettingsReply, request, either.Token);
				// A page that left before the request was registered would never settle it.
				if (!core._messages.TryGetHome(page, out _)) {
					await either.CancelAsync().ConfigureAwait(false);
				}

				return await answer.ConfigureAwait(false);
			} catch (InvalidOperationException failure) when (core._messages.TryGetHome(page, out _)) {
				throw new DeviceUnavailableException($"{home.Name} couldn't answer: {failure.Message}");
			} catch (Exception gone) when (gone is InvalidOperationException or OperationCanceledException && !ct.IsCancellationRequested) {
				throw new DeviceUnavailableException(timeout.IsCancellationRequested
					? $"{home.Name} didn't answer within {core._operationDeadline.TotalSeconds:0} seconds; the setting may or may not have changed."
					: $"{home.Name} disconnected before it answered; the setting may or may not have changed.");
			}
		}
	}
}
