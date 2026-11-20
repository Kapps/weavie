using System.Globalization;
using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.Json;

namespace Weavie.Core.Theming;

/// <summary>
/// The model-facing theme tools, run at a device's home over its installed themes, theme settings and
/// <c>theme-overrides.json</c>. Verb actions (install/select/undo/reset) are commands; see <see cref="ThemeCommands"/>.
/// </summary>
public sealed class ThemeTools {
	private readonly SettingsStore _settings;
	private readonly ThemeOverridesStore _overrides;
	private readonly IThemeScreen _screen;
	private readonly string _hostName;
	private readonly string _where;

	/// <summary>Creates the tools over the home's stores, on the host named <paramref name="hostName"/>.</summary>
	public ThemeTools(SettingsStore settings, ThemeOverridesStore overrides, IThemeScreen screen, string hostName) {
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(overrides);
		ArgumentNullException.ThrowIfNull(screen);
		ArgumentException.ThrowIfNullOrEmpty(hostName);
		_settings = settings;
		_overrides = overrides;
		_screen = screen;
		_hostName = hostName;
		_where = $"on this device ({hostName})";
	}

	/// <summary>Runs one theme tool.</summary>
	public ToolReply Run(ThemeRequest request) {
		ArgumentNullException.ThrowIfNull(request);
		var args = request.Args;
		try {
			return request.Tool switch {
				"listThemes" => List(),
				"describeTheme" => Describe(args),
				"setThemeOverride" => Set(args),
				"applyThemeTransform" => Transform(args),
				"removeThemeOverride" => Remove(args),
				_ => Error($"Unknown theme tool: {request.Tool}"),
			};
		} catch (DeviceUnavailableException ex) {
			return Error(ex.Message);
		}
	}

	/// <summary>
	/// The theme an override edits: the explicit <c>theme</c> argument (e.g. the editor theme), else the theme on
	/// screen; throws <see cref="DeviceUnavailableException"/> in <c>system</c> mode while no page reports one.
	/// </summary>
	public string Target(JsonElement args) =>
		args.GetStringOrNull("theme") is { Length: > 0 } id ? id
		: ThemeSettings.ActiveThemeId(_settings, _screen.Showing)
			?? throw new DeviceUnavailableException(
				$"No page from {_hostName} is open, so which theme 'system' mode shows isn't known. Pass a 'theme' id.");

	private ToolReply List() {
		string? active = ThemeSettings.ActiveThemeId(_settings, _screen.Showing);
		return Ok(JsonWrite.Object(writer => {
			writer.WriteString("device", _hostName);
			writer.WriteStartArray("themes");
			foreach (var theme in ThemeCatalog.List()) {
				writer.WriteStartObject();
				writer.WriteString("id", theme.Id);
				writer.WriteString("label", theme.Label);
				writer.WriteString("type", theme.Type);
				writer.WriteBoolean("builtIn", theme.Namespace is null);
				writer.WriteBoolean("active", theme.Id == active);
				writer.WriteEndObject();
			}

			writer.WriteEndArray();
		}));
	}

	private ToolReply Describe(JsonElement args) {
		string target = Target(args);
		return Ok(JsonWrite.Object(writer => {
			writer.WriteString("device", _hostName);
			writer.WriteString("active", target);
			writer.WriteString("label", ThemeCatalog.List().FirstOrDefault(theme => theme.Id == target)?.Label ?? target);
			writer.WriteString("mode", ThemeSettings.Mode(_settings));
			if (_screen.Showing is { } showing) {
				writer.WriteString("showing", showing);
			}

			writer.WriteString("lightTheme", ThemeSettings.LightThemeId(_settings));
			writer.WriteString("darkTheme", ThemeSettings.DarkThemeId(_settings));
			writer.WriteString("editorLightTheme", ThemeSettings.EditorLightThemeId(_settings));
			writer.WriteString("editorDarkTheme", ThemeSettings.EditorDarkThemeId(_settings));
			writer.WritePropertyName("overrides");
			ThemeJson.WriteOps(writer, _overrides.Get(target));
		}));
	}

	private ToolReply Set(JsonElement args) {
		if (args.GetStringOrNull("key") is not { Length: > 0 } key) {
			return Error("setThemeOverride requires a 'key' (a VS Code color id, e.g. editor.background).");
		}

		string? table = args.GetStringOrNull("table");
		if (table is not (null or "colors" or "tokenColors" or "semanticTokenColors")) {
			return Error("setThemeOverride 'table' must be one of: colors, tokenColors, semanticTokenColors.");
		}

		// An empty 'value' reads as absent; an empty 'fontStyle' clears inherited styles.
		string? value = args.GetStringOrNull("value") is { Length: > 0 } given ? given : null;
		string? fontStyle = args.GetStringOrNull("fontStyle");
		if (value is null && fontStyle is null) {
			return Error("setThemeOverride requires a 'value' (a hex color, e.g. #000000) and/or a 'fontStyle' (e.g. italic).");
		}

		if (fontStyle is not null && table is null or "colors") {
			return Error("setThemeOverride 'fontStyle' applies only to syntax; set 'table' to tokenColors or semanticTokenColors.");
		}

		if (fontStyle is not null && !fontStyle.Split(' ', StringSplitOptions.RemoveEmptyEntries)
			.All(token => token is "italic" or "bold" or "underline" or "strikethrough")) {
			return Error("setThemeOverride 'fontStyle' must be a space-separated subset of: italic, bold, underline, strikethrough (or \"\" to clear inherited styles).");
		}

		string target = Target(args);
		_overrides.Append(target, new ThemeOverrideSet { Table = table, Key = key, Value = value, FontStyle = fontStyle });
		var parts = new List<string>(2);
		if (value is not null) {
			parts.Add($"= {value}");
		}

		if (fontStyle is not null) {
			parts.Add(fontStyle.Length == 0 ? "fontStyle cleared" : $"fontStyle '{fontStyle}'");
		}

		string scope = table is null or "colors" ? string.Empty : $" ({table})";
		return Ok($"Set {key} {string.Join(", ", parts)}{scope} on theme '{target}' {_where}.");
	}

	private ToolReply Transform(JsonElement args) {
		string? op = args.GetStringOrNull("op");
		if (op is not ("darken" or "lighten" or "saturate" or "desaturate" or "contrast")) {
			return Error("applyThemeTransform requires 'op' one of: darken, lighten, saturate, desaturate, contrast.");
		}

		if (!TryGetAmount(args, out double amount) || double.IsNaN(amount) || amount <= 0 || amount > 1) {
			return Error("applyThemeTransform requires 'amount', a number between 0 and 1 (e.g. 0.2 for 20%).");
		}

		string? target = args.GetStringOrNull("target");
		if (target is not (null or "all" or "colors" or "tokenColors" or "semanticTokenColors" or "syntax")) {
			return Error("applyThemeTransform 'target' must be one of: all, colors, tokenColors, semanticTokenColors, syntax.");
		}

		string theme = Target(args);
		_overrides.Append(theme, new ThemeOverrideTransform { Op = op, Amount = amount, Target = target });
		string scope = target is null or "all" ? string.Empty : $" ({target})";
		return Ok($"Applied {op} {amount.ToString(CultureInfo.InvariantCulture)}{scope} to theme '{theme}' {_where}.");
	}

	private ToolReply Remove(JsonElement args) {
		if (args.GetStringOrNull("key") is not { Length: > 0 } key) {
			return Error("removeThemeOverride requires a 'key'.");
		}

		string target = Target(args);
		return Ok(_overrides.RemoveSets(target, key)
			? $"Removed override(s) for {key} on theme '{target}' {_where}."
			: $"No 'set' override for {key} on theme '{target}' {_where}.");
	}

	// The embedded claude routinely stringifies scalars ("0.2"); coerce them.
	private static bool TryGetAmount(JsonElement args, out double value) {
		value = 0;
		if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("amount", out var element)) {
			return false;
		}

		return element.ValueKind == JsonValueKind.Number
			? element.TryGetDouble(out value)
			: element.ValueKind == JsonValueKind.String
				&& double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
	}

	private static ToolReply Ok(string text) => new(text, IsError: false);

	private static ToolReply Error(string text) => new(text, IsError: true);
}
