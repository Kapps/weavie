using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.Theming;

namespace Weavie.Core.Mcp;

// The model-facing theming tools (registry server): list/describe/select themes, layer color overrides on
// the active theme, and install themes from Open VSX. Overrides persist per theme in
// ~/.weavie/theme-overrides.json. Split out of McpServer.cs as a partial to keep the core server focused.
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

	private async Task HandleSelectThemeAsync(WebSocket ws, JsonElement args, string? idRaw, CancellationToken ct) {
		if (_settings is null) {
			await SendToolErrorAsync(ws, idRaw, "Theming is not available.", ct).ConfigureAwait(false);
			return;
		}

		string? id = GetStringArg(args, "id");
		if (string.IsNullOrEmpty(id)) {
			await SendToolErrorAsync(ws, idRaw, "selectTheme requires an 'id'. Call listThemes to see available themes.", ct).ConfigureAwait(false);
			return;
		}

		if (!BuiltInThemes.Contains(id) && OpenVsxThemeInstaller.ListInstalled().All(theme => theme.Id != id)) {
			await SendToolErrorAsync(ws, idRaw, $"Unknown theme '{id}'. Call listThemes to see available themes.", ct).ConfigureAwait(false);
			return;
		}

		try {
			using var doc = JsonDocument.Parse(JsonString(id));
			_settings.Set("theme.active", doc.RootElement);
			await SendToolTextAsync(ws, idRaw, $"Active theme is now '{id}'.", ct).ConfigureAwait(false);
			Emit($"selectTheme {id}");
		} catch (Exception ex) when (ex is UnknownSettingException or SettingValidationException or SettingsFileMalformedException) {
			await SendToolErrorAsync(ws, idRaw, ex.Message, ct).ConfigureAwait(false);
		}
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

	private async Task HandleUndoThemeOverrideAsync(WebSocket ws, string? idRaw, CancellationToken ct) {
		if (_themeOverrides is null) {
			await SendToolErrorAsync(ws, idRaw, "Theming is not available.", ct).ConfigureAwait(false);
			return;
		}

		string active = ActiveThemeId();
		bool undone = _themeOverrides.UndoLast(active);
		await SendToolTextAsync(
			ws, idRaw,
			undone ? $"Undid the last override on theme '{active}'." : $"Theme '{active}' has no overrides to undo.",
			ct).ConfigureAwait(false);
	}

	private async Task HandleResetThemeAsync(WebSocket ws, string? idRaw, CancellationToken ct) {
		if (_themeOverrides is null) {
			await SendToolErrorAsync(ws, idRaw, "Theming is not available.", ct).ConfigureAwait(false);
			return;
		}

		string active = ActiveThemeId();
		bool cleared = _themeOverrides.Clear(active);
		await SendToolTextAsync(
			ws, idRaw,
			cleared ? $"Cleared all overrides on theme '{active}'." : $"Theme '{active}' had no overrides.",
			ct).ConfigureAwait(false);
	}

	private async Task HandleInstallThemeAsync(WebSocket ws, JsonElement args, string? idRaw, CancellationToken ct) {
		string? ns = GetStringArg(args, "namespace");
		string? name = GetStringArg(args, "name");
		string? version = GetStringArg(args, "version");
		if (string.IsNullOrEmpty(ns) || string.IsNullOrEmpty(name)) {
			await SendToolErrorAsync(ws, idRaw, "installTheme requires 'namespace' and 'name' (the Open VSX publisher and extension, e.g. dracula-theme / theme-dracula).", ct).ConfigureAwait(false);
			return;
		}

		try {
			var installer = new OpenVsxThemeInstaller();
			var installed = await installer.InstallAsync(ns, name, string.IsNullOrEmpty(version) ? null : version, ct).ConfigureAwait(false);
			if (installed.Count == 0) {
				await SendToolTextAsync(ws, idRaw, $"Installed {ns}.{name}, but it contributed no color themes.", ct).ConfigureAwait(false);
				return;
			}

			string ids = string.Join(", ", installed.Select(theme => $"'{theme.Id}'"));
			await SendToolTextAsync(ws, idRaw, $"Installed {installed.Count} theme(s) from {ns}.{name}: {ids}. Use selectTheme to switch to one.", ct).ConfigureAwait(false);
			Emit($"installTheme {ns}.{name} -> {installed.Count} theme(s)");
		} catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException) {
			await SendToolErrorAsync(ws, idRaw, $"installTheme failed: {ex.Message}", ct).ConfigureAwait(false);
		}
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
