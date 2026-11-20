using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.Json;

namespace Weavie.Core.Mcp;

// The device tools. Settings: this host answers for its machine and repo keys; every other key goes to the
// session's device. Themes: every call goes to the session's device.
public sealed partial class McpServer {
	private static readonly JsonElement NoThemeArgs = JsonDocument.Parse("{}").RootElement.Clone();

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

		var device = Require(_device, "Settings");
		var reply = operation == "list"
			? new ToolReply(SettingsTools.JoinIndexes(tools.Machine(request, _settingsWorkspaceRoot), await DeviceAsync(() => device.SettingsAsync(request, ct)).ConfigureAwait(false)), IsError: false)
			: request.Key is { } key && tools.OwnsMachineKey(key)
				? tools.Machine(request, _settingsWorkspaceRoot)
				: await DeviceAsync(() => device.SettingsAsync(request, ct)).ConfigureAwait(false);
		if (await SendReplyAsync(responder, idRaw, reply, ct).ConfigureAwait(false) && operation is "set" or "clear") {
			Emit($"{tool} {request.Key}{(value is { } set ? $" = {set.GetRawText()}" : string.Empty)}");
		}
	}

	private async Task HandleThemeToolAsync(
		IMcpResponder responder, string tool, JsonElement args, string? idRaw, CancellationToken ct) {
		var device = Require(_device, "Theming");
		var request = new ThemeRequest(tool, args.ValueKind == JsonValueKind.Object ? args.Clone() : NoThemeArgs);
		await SendReplyAsync(responder, idRaw, await DeviceAsync(() => device.ThemesAsync(request, ct)).ConfigureAwait(false), ct).ConfigureAwait(false);
	}

	// Sends a device tool's reply; true when it succeeded.
	private async Task<bool> SendReplyAsync(IMcpResponder responder, string? idRaw, ToolReply reply, CancellationToken ct) {
		if (reply.IsError) {
			await SendToolErrorAsync(responder, idRaw, reply.Text, ct).ConfigureAwait(false);
		} else {
			await SendToolTextAsync(responder, idRaw, reply.Text, ct).ConfigureAwait(false);
		}

		return !reply.IsError;
	}

	private static async Task<ToolReply> DeviceAsync(Func<Task<ToolReply>> request) {
		try {
			return await request().ConfigureAwait(false);
		} catch (DeviceUnavailableException ex) {
			return new(ex.Message, IsError: true);
		}
	}
}
