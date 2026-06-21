using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.Json;
using Weavie.Core.Theming;

namespace Weavie.Core.Mcp;

// The model-facing theming tools (registry server): the data-shaped operations — list/describe the active
// theme and edit its individual color overrides (set/transform/remove), which persist per theme in
// ~/.weavie/theme-overrides.json. The verb actions (install / install-from-file / select / undo / reset) are
// COMMANDS instead (see ThemeCommands), reached via runCommand.
public sealed partial class McpServer {
	private async Task HandleListThemesAsync(WebSocket ws, string? idRaw, CancellationToken ct) {
		string active = ActiveThemeId();
		string json = JsonWrite.Object(writer => {
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
		var themeOverrides = Require(_themeOverrides, "Theming");
		string active = ActiveThemeId();
		var overrides = themeOverrides.Get(active);
		string json = JsonWrite.Object(writer => {
			writer.WriteString("active", active);
			writer.WriteString("label", ThemeLabel(active));
			// Report the mode + the theme chosen for each polarity so the model can reason about light/dark.
			// 'active' is the concrete theme overrides apply to.
			if (_settings is not null) {
				writer.WriteString("mode", ThemeSettings.Mode(_settings));
				writer.WriteString("lightTheme", ThemeSettings.LightThemeId(_settings));
				writer.WriteString("darkTheme", ThemeSettings.DarkThemeId(_settings));
			}

			writer.WritePropertyName("overrides");
			ThemeJson.WriteOps(writer, overrides);
		});
		await SendToolTextAsync(ws, idRaw, json, ct).ConfigureAwait(false);
	}

	private async Task HandleSetThemeOverrideAsync(WebSocket ws, JsonElement args, string? idRaw, CancellationToken ct) {
		var themeOverrides = Require(_themeOverrides, "Theming");
		string? key = args.GetStringOrNull("key");
		if (string.IsNullOrEmpty(key)) {
			await SendToolErrorAsync(ws, idRaw, "setThemeOverride requires a 'key' (a VS Code color id, e.g. editor.background).", ct).ConfigureAwait(false);
			return;
		}

		string? table = args.GetStringOrNull("table");
		if (table is not (null or "colors" or "tokenColors" or "semanticTokenColors")) {
			await SendToolErrorAsync(ws, idRaw, "setThemeOverride 'table' must be one of: colors, tokenColors, semanticTokenColors.", ct).ConfigureAwait(false);
			return;
		}

		// An op sets a foreground color ('value'), a font style ('fontStyle'), or both — at least one. An empty
		// 'value' reads as absent; an empty 'fontStyle' is meaningful (clears inherited styles), so its presence
		// test is null-vs-not, not IsNullOrEmpty.
		string? value = args.GetStringOrNull("value");
		bool hasValue = !string.IsNullOrEmpty(value);
		string? fontStyle = args.GetStringOrNull("fontStyle");
		bool hasFontStyle = fontStyle is not null;
		if (!hasValue && !hasFontStyle) {
			await SendToolErrorAsync(ws, idRaw, "setThemeOverride requires a 'value' (a hex color, e.g. #000000) and/or a 'fontStyle' (e.g. italic).", ct).ConfigureAwait(false);
			return;
		}

		if (hasFontStyle && table is null or "colors") {
			await SendToolErrorAsync(ws, idRaw, "setThemeOverride 'fontStyle' applies only to syntax; set 'table' to tokenColors or semanticTokenColors.", ct).ConfigureAwait(false);
			return;
		}

		if (hasFontStyle && !IsValidFontStyle(fontStyle!)) {
			await SendToolErrorAsync(ws, idRaw, "setThemeOverride 'fontStyle' must be a space-separated subset of: italic, bold, underline, strikethrough (or \"\" to clear inherited styles).", ct).ConfigureAwait(false);
			return;
		}

		string active = ActiveThemeId();
		themeOverrides.Append(active, new ThemeOverrideSet {
			Table = table,
			Key = key,
			Value = hasValue ? value : null,
			FontStyle = fontStyle,
		});
		await SendToolTextAsync(ws, idRaw, DescribeSet(key, value, hasValue, fontStyle, hasFontStyle, table, active), ct).ConfigureAwait(false);
	}

	// "Set <key> = <color>, fontStyle '<style>' (<table>) on theme '<id>'." — only the parts that were set.
	private static string DescribeSet(string key, string? value, bool hasValue, string? fontStyle, bool hasFontStyle, string? table, string active) {
		var parts = new List<string>(2);
		if (hasValue) {
			parts.Add($"= {value}");
		}

		if (hasFontStyle) {
			parts.Add(fontStyle!.Length == 0 ? "fontStyle cleared" : $"fontStyle '{fontStyle}'");
		}

		string where = table is null or "colors" ? string.Empty : $" ({table})";
		return $"Set {key} {string.Join(", ", parts)}{where} on theme '{active}'.";
	}

	// A valid fontStyle is "" (clear inherited styles) or a space-separated subset of the four TextMate styles.
	private static bool IsValidFontStyle(string fontStyle) {
		foreach (string token in fontStyle.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
			if (token is not ("italic" or "bold" or "underline" or "strikethrough")) {
				return false;
			}
		}

		return true;
	}

	private async Task HandleApplyThemeTransformAsync(WebSocket ws, JsonElement args, string? idRaw, CancellationToken ct) {
		var themeOverrides = Require(_themeOverrides, "Theming");
		string? op = args.GetStringOrNull("op");
		if (string.IsNullOrEmpty(op) || !IsValidTransformOp(op)) {
			await SendToolErrorAsync(ws, idRaw, "applyThemeTransform requires 'op' one of: darken, lighten, saturate, desaturate, contrast.", ct).ConfigureAwait(false);
			return;
		}

		if (!TryGetDoubleArg(args, "amount", out double amount) || double.IsNaN(amount) || amount <= 0 || amount > 1) {
			await SendToolErrorAsync(ws, idRaw, "applyThemeTransform requires 'amount', a number between 0 and 1 (e.g. 0.2 for 20%).", ct).ConfigureAwait(false);
			return;
		}

		string? target = args.GetStringOrNull("target");
		if (target is not (null or "all" or "colors" or "tokenColors" or "semanticTokenColors" or "syntax")) {
			await SendToolErrorAsync(ws, idRaw, "applyThemeTransform 'target' must be one of: all, colors, tokenColors, semanticTokenColors, syntax.", ct).ConfigureAwait(false);
			return;
		}

		string active = ActiveThemeId();
		themeOverrides.Append(active, new ThemeOverrideTransform { Op = op, Amount = amount, Target = target });
		string scope = target is null or "all" ? string.Empty : $" ({target})";
		await SendToolTextAsync(ws, idRaw, $"Applied {op} {amount.ToString(CultureInfo.InvariantCulture)}{scope} to theme '{active}'.", ct).ConfigureAwait(false);
	}

	private async Task HandleRemoveThemeOverrideAsync(WebSocket ws, JsonElement args, string? idRaw, CancellationToken ct) {
		var themeOverrides = Require(_themeOverrides, "Theming");
		string? key = args.GetStringOrNull("key");
		if (string.IsNullOrEmpty(key)) {
			await SendToolErrorAsync(ws, idRaw, "removeThemeOverride requires a 'key'.", ct).ConfigureAwait(false);
			return;
		}

		string active = ActiveThemeId();
		var ops = themeOverrides.Get(active);
		var kept = ops.Where(op => op is not ThemeOverrideSet set || set.Key != key).ToList();
		if (kept.Count == ops.Count) {
			await SendToolTextAsync(ws, idRaw, $"No 'set' override for {key} on theme '{active}'.", ct).ConfigureAwait(false);
			return;
		}

		themeOverrides.SetOps(active, kept);
		await SendToolTextAsync(ws, idRaw, $"Removed override(s) for {key} on theme '{active}'.", ct).ConfigureAwait(false);
	}

	private string ActiveThemeId() =>
		_settings is null ? ThemeSettings.DefaultThemeId : ThemeSettings.ResolveActiveThemeId(_settings);

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

	private static bool TryGetDoubleArg(JsonElement args, string key, out double value) {
		value = 0;
		if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var element)) {
			return false;
		}

		if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value)) {
			return true;
		}

		// The embedded claude routinely stringifies scalars ("0.2"); coerce them.
		return element.ValueKind == JsonValueKind.String
			&& double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
	}
}
