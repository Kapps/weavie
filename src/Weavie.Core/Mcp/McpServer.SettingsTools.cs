using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.Json;

namespace Weavie.Core.Mcp;

// The settings tools: this host answers for its machine and repo keys; every other key goes to the session's device.
public sealed partial class McpServer {
	private async Task HandleSettingsToolAsync(
		IMcpResponder responder, string tool, JsonElement args, string? idRaw, CancellationToken ct) {
		var tools = Require(_settingsTools, "Settings");
		string operation = tool switch { "listSettings" => "list", "getSetting" => "get", "setSetting" => "set", _ => "clear" };
		JsonElement? value = operation == "set" && args.ValueKind == JsonValueKind.Object && args.TryGetProperty("value", out var given)
			? given.Clone()
			: null;
		var request = new SettingsRequest(operation, args.GetStringOrNull("key"), value);
		if (operation != "list" && string.IsNullOrEmpty(request.Key)) {
			await SendToolErrorAsync(responder, idRaw, $"{tool} requires a 'key'.", ct).ConfigureAwait(false);
			return;
		}

		var reply = operation == "list"
			? new SettingsReply(SettingsTools.JoinIndexes(tools.Machine(request, _settingsWorkspaceRoot), await DeviceSettingsAsync(request, ct).ConfigureAwait(false)), IsError: false)
			: request.Key is { } key && tools.OwnsMachineKey(key)
				? tools.Machine(request, _settingsWorkspaceRoot)
				: await DeviceSettingsAsync(request, ct).ConfigureAwait(false);
		if (reply.IsError) {
			await SendToolErrorAsync(responder, idRaw, reply.Text, ct).ConfigureAwait(false);
			return;
		}

		await SendToolTextAsync(responder, idRaw, reply.Text, ct).ConfigureAwait(false);
		if (operation is "set" or "clear") {
			Emit($"{tool} {request.Key}{(value is { } set ? $" = {set.GetRawText()}" : string.Empty)}");
		}
	}

	private async Task<SettingsReply> DeviceSettingsAsync(SettingsRequest request, CancellationToken ct) {
		try {
			return await Require(_deviceSettings, "Settings").RequestAsync(request, ct).ConfigureAwait(false);
		} catch (DeviceUnavailableException ex) {
			return new(ex.Message, IsError: true);
		}
	}
}
