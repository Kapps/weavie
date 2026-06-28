using System.Text.Json;
using Weavie.Core.Json;

namespace Weavie.Core.Configuration;

/// <summary>
/// Typography settings — family, size, weight — for the Monaco editor and xterm terminal. A global
/// <c>font.*</c> default is inherited by both; each surface may override any axis via <c>editor.font.*</c> /
/// <c>terminal.font.*</c>, with an "inherit" sentinel (empty family/weight, <c>0</c> size) falling through.
/// All are <see cref="ApplyMode.Live"/>: a change re-pushes the resolved fonts to the web.
/// </summary>
public static class FontSettings {
	/// <summary>Global font family (a CSS font-family stack) inherited by both surfaces.</summary>
	public const string GlobalFamily = "font.family";

	/// <summary>Global font size in CSS px inherited by both surfaces.</summary>
	public const string GlobalSize = "font.size";

	/// <summary>Global font weight inherited by both surfaces.</summary>
	public const string GlobalWeight = "font.weight";

	/// <summary>Editor font family override; empty inherits <see cref="GlobalFamily"/>.</summary>
	public const string EditorFamily = "editor.font.family";

	/// <summary>Editor font size override; <c>0</c> inherits <see cref="GlobalSize"/>.</summary>
	public const string EditorSize = "editor.font.size";

	/// <summary>Editor font weight override; <c>inherit</c> uses <see cref="GlobalWeight"/>.</summary>
	public const string EditorWeight = "editor.font.weight";

	/// <summary>Terminal font family override; empty inherits <see cref="GlobalFamily"/>.</summary>
	public const string TerminalFamily = "terminal.font.family";

	/// <summary>Terminal font size override; <c>0</c> inherits <see cref="GlobalSize"/>.</summary>
	public const string TerminalSize = "terminal.font.size";

	/// <summary>Terminal font weight override; <c>inherit</c> uses <see cref="GlobalWeight"/>.</summary>
	public const string TerminalWeight = "terminal.font.weight";

	/// <summary>The "inherit the global value" sentinel for the weight overrides.</summary>
	public const string InheritWeight = "inherit";

	/// <summary>Every font setting key — the host subscribes to all of them to re-push on any change.</summary>
	public static readonly IReadOnlyList<string> Keys = [
		GlobalFamily, GlobalSize, GlobalWeight,
		EditorFamily, EditorSize, EditorWeight,
		TerminalFamily, TerminalSize, TerminalWeight,
	];

	// Cross-platform monospace stack, ending in generic monospace so it never silently fails.
	private const string DefaultFamily =
		"""ui-monospace, "Cascadia Code", "SF Mono", Menlo, Consolas, "Courier New", monospace""";

	// Leads with the bundled JetBrains Mono (see web/src/fonts.css) so it renders even where it isn't installed.
	private const string DefaultTerminalFamily =
		"\"JetBrains Mono\", ui-monospace, \"Cascadia Code\", Consolas, monospace";

	// Leads with the bundled Go Mono (see web/src/fonts.css) so it renders even where it isn't installed.
	private const string DefaultEditorFamily =
		"\"Go Mono\", ui-monospace, \"Cascadia Code\", Consolas, monospace";

	/// <summary>The default global font size (px), and the value the "reset font size" command restores.</summary>
	public const long DefaultSize = 16;
	private const string DefaultWeight = "normal";
	private const long MinSize = 6;
	private const long MaxSize = 72;

	/// <summary>Clamps a font size to the allowed range, so the zoom commands stop at the bounds (never an error).</summary>
	public static long ClampSize(long size) => Math.Clamp(size, MinSize, MaxSize);

	// CSS-recognized weights (keywords + numeric scale), mapped 1:1 onto Monaco's and xterm's weight strings.
	private static readonly string[] Weights =
		["normal", "bold", "100", "200", "300", "400", "500", "600", "700", "800", "900"];

	/// <summary>The resolved typography for one surface, after global/override inheritance.</summary>
	public readonly record struct ResolvedFont(string Family, long Size, string Weight);

	/// <summary>Registers the nine font settings (global + per-surface overrides) into <paramref name="registry"/>.</summary>
	public static void Register(SettingsRegistry registry) {
		ArgumentNullException.ThrowIfNull(registry);

		registry.Register(new SettingDefinition {
			Key = GlobalFamily,
			Kind = SettingKind.String,
			Description = "Font family for the editor and terminal (a CSS font-family stack; the editor "
				+ "and terminal can override it).",
			Aliases = ["font", "font family", "typeface"],
			Apply = ApplyMode.Live,
			Default = DefaultFamily,
		});

		registry.Register(new SettingDefinition {
			Key = GlobalSize,
			Kind = SettingKind.Int,
			Description = "Font size in pixels for the editor and terminal (overridable per surface).",
			Aliases = ["font size", "text size"],
			Apply = ApplyMode.Live,
			Default = DefaultSize,
			Validate = ValidateSize,
		});

		registry.Register(new SettingDefinition {
			Key = GlobalWeight,
			Kind = SettingKind.String,
			Description = "Font weight for the editor and terminal (overridable per surface).",
			Aliases = ["font weight", "boldness"],
			AllowedValues = Weights,
			Apply = ApplyMode.Live,
			Default = DefaultWeight,
		});

		RegisterOverride(registry, EditorFamily, EditorSize, EditorWeight, "editor", ["editor font"], DefaultEditorFamily);
		RegisterOverride(
			registry, TerminalFamily, TerminalSize, TerminalWeight, "terminal", ["terminal font"], DefaultTerminalFamily);
	}

	/// <summary>The editor's effective font after applying its overrides over the global default.</summary>
	public static ResolvedFont ResolveEditor(SettingsStore store) =>
		Resolve(store, EditorFamily, EditorSize, EditorWeight);

	/// <summary>The terminal's effective font after applying its overrides over the global default.</summary>
	public static ResolvedFont ResolveTerminal(SettingsStore store) =>
		Resolve(store, TerminalFamily, TerminalSize, TerminalWeight);

	/// <summary>
	/// Serializes the resolved editor + terminal fonts as JSON. With <paramref name="messageType"/> set, a
	/// <c>"type"</c> field is written first (for a bridge push); when null, the bare object is produced (for the
	/// injected <c>window.__WEAVIE_FONTS__</c> global).
	/// </summary>
	public static string BuildJson(SettingsStore store, string? messageType) {
		ArgumentNullException.ThrowIfNull(store);
		var editor = ResolveEditor(store);
		var terminal = ResolveTerminal(store);
		return JsonWrite.Object(writer => {
			if (messageType is not null) {
				writer.WriteString("type", messageType);
			}

			WriteFont(writer, "editor", editor);
			WriteFont(writer, "terminal", terminal);
		});
	}

	private static void RegisterOverride(
		SettingsRegistry registry, string familyKey, string sizeKey, string weightKey, string surface,
		IReadOnlyList<string> aliasRoots, string familyDefault) {
		registry.Register(new SettingDefinition {
			Key = familyKey,
			Kind = SettingKind.String,
			Description = $"Font family for the {surface} only; empty inherits the global font.family.",
			Aliases = [.. aliasRoots, $"{surface} font family"],
			Apply = ApplyMode.Live,
			Default = familyDefault,
		});

		registry.Register(new SettingDefinition {
			Key = sizeKey,
			Kind = SettingKind.Int,
			Description = $"Font size in pixels for the {surface} only; 0 inherits the global font.size.",
			Aliases = [$"{surface} font size"],
			Apply = ApplyMode.Live,
			Default = 0L,
			Validate = ValidateOverrideSize,
		});

		registry.Register(new SettingDefinition {
			Key = weightKey,
			Kind = SettingKind.String,
			Description = $"Font weight for the {surface} only; 'inherit' uses the global font.weight.",
			Aliases = [$"{surface} font weight"],
			AllowedValues = [InheritWeight, .. Weights],
			Apply = ApplyMode.Live,
			Default = InheritWeight,
		});
	}

	private static ResolvedFont Resolve(SettingsStore store, string familyKey, string sizeKey, string weightKey) {
		string family = FirstNonEmpty(store.GetString(familyKey), store.GetString(GlobalFamily)) ?? DefaultFamily;

		long sizeOverride = store.GetInt(sizeKey, 0);
		long size = sizeOverride > 0 ? sizeOverride : PositiveOr(store.GetInt(GlobalSize, 0), DefaultSize);

		string? weightOverride = store.GetString(weightKey);
		string weight = !string.IsNullOrEmpty(weightOverride) && weightOverride != InheritWeight
			? weightOverride
			: FirstNonEmpty(store.GetString(GlobalWeight)) ?? DefaultWeight;

		return new ResolvedFont(family, size, weight);
	}

	private static ValidationResult ValidateSize(object? value) =>
		value is long size && size is >= MinSize and <= MaxSize
			? ValidationResult.Success
			: ValidationResult.Failure($"font size must be between {MinSize} and {MaxSize} pixels.");

	private static ValidationResult ValidateOverrideSize(object? value) =>
		value is long size && (size == 0 || size is >= MinSize and <= MaxSize)
			? ValidationResult.Success
			: ValidationResult.Failure($"font size override must be 0 (inherit) or {MinSize}–{MaxSize} pixels.");

	private static void WriteFont(Utf8JsonWriter writer, string name, ResolvedFont font) {
		writer.WriteStartObject(name);
		writer.WriteString("family", font.Family);
		writer.WriteNumber("size", font.Size);
		writer.WriteString("weight", font.Weight);
		writer.WriteEndObject();
	}

	private static string? FirstNonEmpty(params string?[] values) {
		foreach (string? value in values) {
			if (!string.IsNullOrWhiteSpace(value)) {
				return value;
			}
		}

		return null;
	}

	private static long PositiveOr(long value, long fallback) => value > 0 ? value : fallback;
}
