using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.Theming;

namespace Weavie.Core.Mcp;

// The model-facing theming tools (registry server): the data-shaped operations — list/describe the active
// theme and edit its individual color overrides (set/transform/remove), which persist per theme in
// ~/.weavie/theme-overrides.json. The verb actions (install / install-from-file / select / undo / reset) are
// COMMANDS instead (see ThemeCommands), reached via runCommand. Split from McpServer.cs to keep it focused.
public sealed partial class McpServer {
	private async Task HandleListThemesAsync(WebSocket ws, string? idRaw, CancellationToken ct) {
		string active = ActiveThemeId();
		string json = WriteJson(writer => {
			writer.WriteStartArray("themes");
			foreach (var (id, label, type) in BuiltInThemes.All) {
				writer.WriteStartObject();
				writer.WriteString("id", id);
				writer.WriteString("label", label);
				writer.WriteString("type", type);
				writer.WriteBoolean("builtIn", true);
				writer.WriteBoolean("active", id == active);
				writer.WriteEndObject();
			}

			foreach (var theme in OpenVsxThemeInstaller.ListInstalled()) {
				writer.WriteStartObject();
				writer.WriteString("id", theme.Id);
				writer.WriteString("label", theme.Label);
				writer.WriteString("type", theme.UiTheme);
				writer.WriteBoolean("builtIn", false);
				writer.WriteBoolean("active", theme.Id == active);
				writer.WriteEndObject();
			}

			writer.WriteEndArray();
		});
		await SendToolTextAsync(ws, idRaw, json, ct).ConfigureAwait(false);
	}

	private async Task HandleDescribeThemeAsync(WebSocket ws, string? idRaw, CancellationToken ct) {
		if (_themeOverrides is null) {
			await SendToolErrorAsync(ws, idRaw, "Theming is not available.", ct).ConfigureAwait(false);
			return;
		}

		string active = ActiveThemeId();
		var overrides = _themeOverrides.Get(active);
		string json = WriteJson(writer => {
			writer.WriteString("active", active);
			writer.WriteString("label", ThemeLabel(active));
			writer.WritePropertyName("overrides");
			ThemeJson.WriteOps(writer, overrides);
		});
		await SendToolTextAsync(ws, idRaw, json, ct).ConfigureAwait(false);
	}

	private async Task HandleSetThemeOverrideAsync(WebSocket ws, JsonElement args, string? idRaw, CancellationToken ct) {
		if (_themeOverrides is null) {
			await SendToolErrorAsync(ws, idRaw, "Theming is not available.", ct).ConfigureAwait(false);
			return;
		}

		string? key = GetStringArg(args, "key");
		string? value = GetStringArg(args, "value");
		if (string.IsNullOrEmpty(key)) {
			await SendToolErrorAsync(ws, idRaw, "setThemeOverride requires a 'key' (a VS Code color id, e.g. editor.background).", ct).ConfigureAwait(false);
			return;
		}

		if (string.IsNullOrEmpty(value)) {
			await SendToolErrorAsync(ws, idRaw, "setThemeOverride requires a 'value' (a hex color, e.g. #000000).", ct).ConfigureAwait(false);
			return;
		}

		string? table = GetStringArg(args, "table");
		if (table is not (null or "colors" or "tokenColors" or "semanticTokenColors")) {
			await SendToolErrorAsync(ws, idRaw, "setThemeOverride 'table' must be one of: colors, tokenColors, semanticTokenColors.", ct).ConfigureAwait(false);
			return;
		}

		string active = ActiveThemeId();
		_themeOverrides.Append(active, new ThemeOverrideSet { Table = table, Key = key, Value = value });
		string where = table is null or "colors" ? string.Empty : $" ({table})";
		await SendToolTextAsync(ws, idRaw, $"Set {key} = {value}{where} on theme '{active}'.", ct).ConfigureAwait(false);
	}

	private async Task HandleApplyThemeTransformAsync(WebSocket ws, JsonElement args, string? idRaw, CancellationToken ct) {
		if (_themeOverrides is null) {
			await SendToolErrorAsync(ws, idRaw, "Theming is not available.", ct).ConfigureAwait(false);
			return;
		}

		string? op = GetStringArg(args, "op");
		if (string.IsNullOrEmpty(op) || !IsValidTransformOp(op)) {
			await SendToolErrorAsync(ws, idRaw, "applyThemeTransform requires 'op' one of: darken, lighten, saturate, desaturate, contrast.", ct).ConfigureAwait(false);
			return;
		}

		if (!TryGetDoubleArg(args, "amount", out double amount) || double.IsNaN(amount) || amount <= 0 || amount > 1) {
			await SendToolErrorAsync(ws, idRaw, "applyThemeTransform requires 'amount', a number between 0 and 1 (e.g. 0.2 for 20%).", ct).ConfigureAwait(false);
			return;
		}

		string? target = GetStringArg(args, "target");
		if (target is not (null or "all" or "colors" or "tokenColors" or "semanticTokenColors" or "syntax")) {
			await SendToolErrorAsync(ws, idRaw, "applyThemeTransform 'target' must be one of: all, colors, tokenColors, semanticTokenColors, syntax.", ct).ConfigureAwait(false);
			return;
		}

		string active = ActiveThemeId();
		_themeOverrides.Append(active, new ThemeOverrideTransform { Op = op, Amount = amount, Target = target });
		string scope = target is null or "all" ? string.Empty : $" ({target})";
		await SendToolTextAsync(ws, idRaw, $"Applied {op} {amount.ToString(CultureInfo.InvariantCulture)}{scope} to theme '{active}'.", ct).ConfigureAwait(false);
	}

	private async Task HandleRemoveThemeOverrideAsync(WebSocket ws, JsonElement args, string? idRaw, CancellationToken ct) {
		if (_themeOverrides is null) {
			await SendToolErrorAsync(ws, idRaw, "Theming is not available.", ct).ConfigureAwait(false);
			return;
		}

		string? key = GetStringArg(args, "key");
		if (string.IsNullOrEmpty(key)) {
			await SendToolErrorAsync(ws, idRaw, "removeThemeOverride requires a 'key'.", ct).ConfigureAwait(false);
			return;
		}

		string active = ActiveThemeId();
		var ops = _themeOverrides.Get(active);
		var kept = ops.Where(op => op is not ThemeOverrideSet set || set.Key != key).ToList();
		if (kept.Count == ops.Count) {
			await SendToolTextAsync(ws, idRaw, $"No 'set' override for {key} on theme '{active}'.", ct).ConfigureAwait(false);
			return;
		}

		_themeOverrides.SetOps(active, kept);
		await SendToolTextAsync(ws, idRaw, $"Removed override(s) for {key} on theme '{active}'.", ct).ConfigureAwait(false);
	}

	private string ActiveThemeId() => _settings?.GetString("theme.active") ?? ThemeSettings.DefaultThemeId;

	private static string ThemeLabel(string id) {
		foreach (var (builtInId, label, _) in BuiltInThemes.All) {
			if (builtInId == id) {
				return label;
			}
		}

		return OpenVsxThemeInstaller.ListInstalled().FirstOrDefault(theme => theme.Id == id)?.Label ?? id;
	}

	private static bool IsValidTransformOp(string op) =>
		op is "darken" or "lighten" or "saturate" or "desaturate" or "contrast";

	private static string? GetStringArg(JsonElement args, string key) =>
		args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	private static bool TryGetDoubleArg(JsonElement args, string key, out double value) {
		value = 0;
		if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var element)) {
			return false;
		}

		if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value)) {
			return true;
		}

		// The embedded claude routinely stringifies scalars ("0.2"); coerce like the settings boundary does.
		return element.ValueKind == JsonValueKind.String
			&& double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
	}
}
