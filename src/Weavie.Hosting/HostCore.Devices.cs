using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

// Devices: this host answers device tools for the pages it serves, and relays a session's device requests to a page
// from that device's home. See docs/specs/settings-ownership.md.
public sealed partial class HostCore {
	private void WireDeviceMessages() {
		var device = _messages.Host.Feature("device");
		device.Handle(
			"settings", MessageActivity.ActionFailed("Reaching device settings"), WireJson.Default.SettingsRequest, WireJson.Default.ToolReply,
			(request, ct) => _homeDevice.SettingsAsync(request, ct));
		device.Handle(
			"themes", MessageActivity.ActionFailed("Reaching device themes"), WireJson.Default.ThemeRequest, WireJson.Default.ToolReply,
			(request, ct) => _homeDevice.ThemesAsync(request, ct));
		device.Handle(
			"keybindings", MessageActivity.ActionFailed("Reading device key bindings"), WireJson.Default.EmptyPayload, WireJson.Default.IReadOnlyListResolvedKeybinding,
			(_, ct) => _homeDevice.KeybindingsAsync(ct));
		device.HandleOwned("showing", MessageActivity.Droppable("Noting the theme on screen"), WireJson.Default.ThemeShowing, (message, page, _) => {
			_messages.Show(page, message.Polarity);
			return Task.CompletedTask;
		});
	}

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

	/// <summary>The polarity a page reports it is showing: <c>light</c> or <c>dark</c>.</summary>
	internal sealed record ThemeShowing(string Polarity);

	private sealed class BoundDevice(HostCore core, SessionDevice device) : IDevice {
		public Task<ToolReply> SettingsAsync(SettingsRequest request, CancellationToken ct) => device.Current.Home is null
			? core._homeDevice.SettingsAsync(request, ct)
			: PageAsync("settings", WireJson.Default.SettingsRequest, WireJson.Default.ToolReply, request, deadline: true, ct);

		public Task<ToolReply> ThemesAsync(ThemeRequest request, CancellationToken ct) => device.Current.Home is null
			? core._homeDevice.ThemesAsync(request, ct)
			: PageAsync("themes", WireJson.Default.ThemeRequest, WireJson.Default.ToolReply, request, deadline: true, ct);

		public Task<IReadOnlyList<ResolvedKeybinding>> KeybindingsAsync(CancellationToken ct) => device.Current.Home is null
			? core._homeDevice.KeybindingsAsync(ct)
			: PageAsync("keybindings", WireJson.Default.EmptyPayload, WireJson.Default.IReadOnlyListResolvedKeybinding, EmptyPayload.Value, deadline: true, ct);

		// A client-owned command an agent runs: a page of the device runs it, as that page's own palette would.
		public async Task<CommandResult> CommandAsync(string id, string? argsJson, CancellationToken ct) {
			JsonElement? args = null;
			if (!string.IsNullOrWhiteSpace(argsJson)) {
				using var document = JsonDocument.Parse(argsJson);
				args = document.RootElement.Clone();
			}

			bool deadline = core._commandRegistry.Require(id).OnTimeout != OperationSeverity.NoDeadline;
			try {
				return FromWireResult(await PageAsync(
					"command", WireJson.Default.CommandRequest, WireJson.Default.CommandWireResult, new CommandRequest(id, args), deadline, ct)
					.ConfigureAwait(false));
			} catch (DeviceUnavailableException unavailable) {
				return CommandResult.Failure(unavailable.Message);
			}
		}

		private async Task<TResponse> PageAsync<TRequest, TResponse>(
			string name, JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResponse> responseType, TRequest request, bool deadline, CancellationToken ct) {
			var (home, claimant) = device.Current;
			string here = core._settingsTools.HostName;
			string there = home?.Name ?? here;
			if (core._messages.PageFrom(home, claimant) is not { } page) {
				throw new DeviceUnavailableException(home is null
					? $"No page from {here} is open, so its device can't be reached from this session."
					: $"{there} isn't connected to {here}, so its device can't be reached from this session.");
			}

			using var timeout = deadline ? new CancellationTokenSource(core._operationDeadline, core._time) : new CancellationTokenSource();
			using var either = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
			try {
				var answer = core._messages.Host.RequestAsync(page, "device", name, requestType, responseType, request, either.Token);
				// A page that left before the request was registered would never settle it.
				if (!core._messages.TryGetHome(page, out _)) {
					await either.CancelAsync().ConfigureAwait(false);
				}

				return await answer.ConfigureAwait(false);
			} catch (InvalidOperationException failure) when (core._messages.TryGetHome(page, out _)) {
				throw new DeviceUnavailableException($"{there} couldn't answer: {failure.Message}");
			} catch (Exception gone) when (gone is InvalidOperationException or OperationCanceledException && !ct.IsCancellationRequested) {
				throw new DeviceUnavailableException(timeout.IsCancellationRequested
					? $"{there} didn't answer within {core._operationDeadline.TotalSeconds:0} seconds; it may or may not have taken effect."
					: $"{there} disconnected before it answered; it may or may not have taken effect.");
			}
		}
	}
}
