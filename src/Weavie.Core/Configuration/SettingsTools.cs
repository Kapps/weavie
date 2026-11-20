using System.Text;
using System.Text.Json;
using Weavie.Core.Commands;

namespace Weavie.Core.Configuration;

/// <summary>One settings tool call: <c>list</c>, <c>get</c>, <c>set</c> or <c>clear</c>.</summary>
public sealed record SettingsRequest(string Operation, string? Key, JsonElement? Value);

/// <summary>
/// The settings tools over one host's store, split by owner: the device keys this host is home to, and the
/// machine and repo keys it runs sessions with. Neither side answers for the other's keys.
/// </summary>
public sealed class SettingsTools {
	private readonly SettingsStore _store;

	/// <summary>Creates the tools over <paramref name="store"/> on the host named <paramref name="hostName"/>.</summary>
	public SettingsTools(SettingsStore store, string hostName) {
		ArgumentNullException.ThrowIfNull(store);
		ArgumentException.ThrowIfNullOrEmpty(hostName);
		_store = store;
		HostName = hostName;
	}

	/// <summary>The name this host's results report.</summary>
	public string HostName { get; }

	/// <summary>Whether <paramref name="key"/> is a machine or repo setting of this host.</summary>
	public bool OwnsMachineKey(string key) =>
		_store.Registry.TryGet(key, out var definition) && definition.Owner != SettingOwner.Device;

	/// <summary>Answers for this host's device settings; a machine or repo key is refused.</summary>
	public ToolReply Device(SettingsRequest request) => Run(request, repoRoot: null);

	/// <summary>Answers for this host's machine settings and <paramref name="repoRoot"/>'s repo settings; a device key is refused.</summary>
	public ToolReply Machine(SettingsRequest request, string repoRoot) {
		ArgumentException.ThrowIfNullOrEmpty(repoRoot);
		return Run(request, repoRoot);
	}

	/// <summary>Joins a machine index with the device's, or with the reason the device's is missing.</summary>
	public static string JoinIndexes(ToolReply machine, ToolReply device) {
		ArgumentNullException.ThrowIfNull(machine);
		ArgumentNullException.ThrowIfNull(device);
		using var machineIndex = JsonDocument.Parse(machine.Text);
		using var deviceIndex = device.IsError ? null : JsonDocument.Parse(device.Text);
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream)) {
			writer.WriteStartObject();
			writer.WriteString("machine", machineIndex.RootElement.GetProperty("host").GetString());
			if (deviceIndex is null) {
				writer.WriteString("deviceUnavailable", device.Text);
			} else {
				writer.WriteString("device", deviceIndex.RootElement.GetProperty("host").GetString());
			}

			writer.WriteStartArray("settings");
			foreach (var index in new[] { machineIndex, deviceIndex }) {
				foreach (var row in index?.RootElement.GetProperty("settings").EnumerateArray() ?? []) {
					row.WriteTo(writer);
				}
			}

			writer.WriteEndArray();
			writer.WriteEndObject();
		}

		return Encoding.UTF8.GetString(stream.ToArray());
	}

	// repoRoot is null for the device side, which has no repo overlay.
	private ToolReply Run(SettingsRequest request, string? repoRoot) {
		ArgumentNullException.ThrowIfNull(request);
		bool device = repoRoot is null;
		try {
			if (request.Operation == "list") {
				string rows = _store.BuildIndexJson(repoRoot, definition => definition.Owner == SettingOwner.Device == device);
				return new($"{{\"host\":\"{JsonEncodedText.Encode(HostName)}\",{rows[1..]}", IsError: false);
			}

			if (request.Key is not { Length: > 0 } key) {
				return new($"{request.Operation} requires a 'key'.", IsError: true);
			}

			if (_store.Registry.Require(key).Owner == SettingOwner.Device != device) {
				return new(device ? $"{key} is not a device setting." : $"{key} is a device setting.", IsError: true);
			}

			string where = device ? $"on this device ({HostName})" : $"on this machine ({HostName})";
			return request.Operation switch {
				"get" => new(repoRoot is null ? _store.BuildGetJson(key) : _store.BuildGetJson(key, repoRoot), IsError: false),
				"set" when request.Value is { } value => new(
					SetSummary(key, value, repoRoot is null ? _store.Set(key, value) : _store.Set(key, value, repoRoot), where),
					IsError: false),
				"set" => new("set requires a 'value'.", IsError: true),
				"clear" => new(ClearSummary(key, repoRoot is null ? _store.Clear(key) : _store.Clear(key, repoRoot), where), IsError: false),
				_ => new($"Unknown settings operation: {request.Operation}", IsError: true),
			};
		} catch (Exception ex) when (ex is UnknownSettingException or SettingValidationException or SettingsFileMalformedException) {
			return new(ex.Message, IsError: true);
		}
	}

	private static string SetSummary(string key, JsonElement value, SetResult result, string where) =>
		$"Set {key} to {value.GetRawText()} {where}.{ApplyNote(result.Apply)}{ShadowNote(result.ShadowedByEnv)}";

	private static string ClearSummary(string key, ClearResult result, string where) => result.Removed
		? $"Cleared {key} {where}; it now falls back to its default.{ApplyNote(result.Apply)}{ShadowNote(result.ShadowedByEnv)}"
		: $"{key} had no user-file override to clear {where}; it was already at its default or env value.{ShadowNote(result.ShadowedByEnv)}";

	private static string ShadowNote(string? variable) => variable is null
		? string.Empty
		: $" Note: {variable} is set and overrides the file, so the effective value is unchanged until you unset it.";

	private static string ApplyNote(ApplyMode apply) => apply switch {
		ApplyMode.ReopensTerminal => " The terminal pane will reopen to apply.",
		ApplyMode.NextSession => " It applies to the next session that starts.",
		ApplyMode.NextLanguageServerStart => $" Run {CoreCommands.RestartLanguageServers} to apply it to running language servers.",
		ApplyMode.RestartRequired => " Restart weavie to apply.",
		_ => " It is live now.",
	};
}
