using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Theming;

/// <summary>
/// Builds the theme payload the host injects (<c>window.__WEAVIE_THEME__</c>) and pushes to the web. Shape is
/// <c>{ mode, light: Slot, dark: Slot }</c>, each <c>Slot</c> = <c>{ id, ops, theme? }</c> (merged theme JSON only
/// for installed themes; built-ins ship by id). Both polarities are sent so the web resolves <c>system</c> and
/// switches light↔dark with no flash. Hand-written via <see cref="Utf8JsonWriter"/> for trim/AOT safety.
/// </summary>
public static class ThemeJson {
	/// <summary>
	/// Builds the theme payload as a JSON string. Null <paramref name="messageType"/> yields the bare object for
	/// injection; non-null adds a <c>"type"</c> field for a bridge push. A theme that fails to load is logged and
	/// its slot's <c>theme</c> field omitted, so the web falls back to the built-in default rather than a blank UI.
	/// </summary>
	public static string Build(
		SettingsStore settings,
		ThemeOverridesStore overrides,
		string? messageType,
		Action<string>? log) {
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(overrides);

		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream)) {
			writer.WriteStartObject();
			if (messageType is not null) {
				writer.WriteString("type", messageType);
			}

			writer.WriteString("mode", ThemeSettings.Mode(settings));
			WriteSlot(writer, "light", ThemeSettings.LightThemeId(settings), overrides, log);
			WriteSlot(writer, "dark", ThemeSettings.DarkThemeId(settings), overrides, log);

			writer.WriteEndObject();
		}

		return Encoding.UTF8.GetString(stream.ToArray());
	}

	// Writes one polarity slot: { id, ops, theme? }. Theme JSON is shipped only for installed themes.
	private static void WriteSlot(
		Utf8JsonWriter writer, string name, string id, ThemeOverridesStore overrides, Action<string>? log) {
		writer.WritePropertyName(name);
		writer.WriteStartObject();
		writer.WriteString("id", id);
		writer.WritePropertyName("ops");
		WriteOps(writer, overrides.Get(id));
		var themeJson = ResolveInstalledThemeJson(id, log);
		if (themeJson is not null) {
			writer.WritePropertyName("theme");
			themeJson.WriteTo(writer);
		}

		writer.WriteEndObject();
	}

	// Serializes the override op list to the web's OverrideOp shape (kind-discriminated), by hand for
	// trim/AOT safety. Shared with the MCP describeTheme tool.
	internal static void WriteOps(Utf8JsonWriter writer, IReadOnlyList<ThemeOverrideOp> ops) {
		writer.WriteStartArray();
		foreach (var op in ops) {
			writer.WriteStartObject();
			switch (op) {
				case ThemeOverrideSet set:
					writer.WriteString("kind", "set");
					if (set.Table is not null) {
						writer.WriteString("table", set.Table);
					}

					writer.WriteString("key", set.Key);
					if (set.Value is not null) {
						writer.WriteString("value", set.Value);
					}

					if (set.FontStyle is not null) {
						writer.WriteString("fontStyle", set.FontStyle);
					}

					break;
				case ThemeOverrideTransform transform:
					writer.WriteString("kind", "transform");
					writer.WriteString("op", transform.Op);
					writer.WriteNumber("amount", transform.Amount);
					if (transform.Target is not null) {
						writer.WriteString("target", transform.Target);
					}

					break;
			}

			writer.WriteEndObject();
		}

		writer.WriteEndArray();
	}

	private static JsonObject? ResolveInstalledThemeJson(string id, Action<string>? log) {
		// Only installed themes need their JSON shipped; not in the index ⇒ a built-in, where id alone suffices.
		var installed = OpenVsxThemeInstaller.ListInstalled().FirstOrDefault(t => t.Id == id);
		if (installed is null) {
			return null;
		}

		try {
			return new ThemeJsonLoader(new LocalFileSystem()).LoadMerged(installed.Path);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException) {
			log?.Invoke($"[theme] could not load installed theme '{id}' from {installed.Path}: {ex.Message}; falling back to the built-in default.");
			return null;
		}
	}
}
