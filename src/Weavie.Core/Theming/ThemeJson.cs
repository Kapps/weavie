using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Theming;

/// <summary>
/// Builds the active-theme payload the host injects (<c>window.__WEAVIE_THEME__</c>) and pushes
/// (<c>{ "type": "theme" }</c>) to the web — the theming analogue of <see cref="FontSettings"/>.BuildJson.
/// Shape (matches the web controller + bridge): <c>{ id, ops, theme? }</c> where <c>id</c> is the active
/// theme id (a setting), <c>ops</c> is that theme's ordered override stack, and <c>theme</c> is the merged
/// VS Code theme JSON — present only for INSTALLED themes (built-ins like <c>weavie-dark</c> ship in the web
/// bundle, so only their id is sent). Written with <see cref="Utf8JsonWriter"/> for trim/AOT safety (the
/// macOS host can't use reflection-based <c>JsonSerializer.Serialize</c>).
/// </summary>
public static class ThemeJson {
	/// <summary>
	/// Builds the theme payload as a JSON string. With <paramref name="messageType"/> null it's the bare
	/// object for injection; non-null adds a <c>"type"</c> field for a bridge push. A failure to load the
	/// active installed theme is logged via <paramref name="log"/> (observable, never silent) and the
	/// <c>theme</c> field is omitted, so the web falls back to the built-in default rather than a blank UI.
	/// </summary>
	public static string Build(
		SettingsStore settings,
		ThemeOverridesStore overrides,
		string? messageType = null,
		Action<string>? log = null) {
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(overrides);

		string id = settings.GetString("theme.active") ?? ThemeSettings.DefaultThemeId;
		var ops = overrides.Get(id);
		var themeJson = ResolveInstalledThemeJson(id, log);

		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream)) {
			writer.WriteStartObject();
			if (messageType is not null) {
				writer.WriteString("type", messageType);
			}

			writer.WriteString("id", id);
			writer.WritePropertyName("ops");
			WriteOps(writer, ops);
			if (themeJson is not null) {
				writer.WritePropertyName("theme");
				themeJson.WriteTo(writer);
			}

			writer.WriteEndObject();
		}

		return Encoding.UTF8.GetString(stream.ToArray());
	}

	// Serializes the override op list to exactly the web's OverrideOp shape (kind-discriminated), by hand so
	// no reflection-based serializer is needed (trim/AOT safety, matching the rest of this class). Shared
	// with the MCP describeTheme tool.
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
					writer.WriteString("value", set.Value);
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
		// Built-ins ship in the web bundle (the controller resolves them by id); only installed themes need
		// their JSON shipped over. Not in the index ⇒ a built-in ⇒ id alone is enough.
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
