using System.Text;
using System.Text.Json;

namespace Weavie.Core.Commands;

/// <summary>
/// Serializes the command catalog and the resolved keybindings to JSON — the one source used by both the
/// MCP <c>listCommands</c> tool and the web injection (<c>__WEAVIE_COMMANDS__</c> / <c>__WEAVIE_KEYBINDINGS__</c>)
/// plus the live <c>commands</c> push. Mirrors <c>SettingsStore.BuildCatalogJson</c>'s Utf8JsonWriter style.
/// </summary>
public static class CommandCatalog {
	/// <summary>
	/// Builds the commands array: each command's id/title/category/description/aliases/runsIn/showInPalette,
	/// its <c>when</c> and args schema when present, and the keys it is currently bound to (from
	/// <paramref name="bindings"/>).
	/// </summary>
	public static string BuildCommandsArrayJson(
		IReadOnlyList<CommandDefinition> definitions,
		IReadOnlyList<ResolvedKeybinding> bindings) {
		ArgumentNullException.ThrowIfNull(definitions);
		ArgumentNullException.ThrowIfNull(bindings);

		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream)) {
			writer.WriteStartArray();
			foreach (var definition in definitions) {
				writer.WriteStartObject();
				writer.WriteString("id", definition.Id);
				writer.WriteString("title", definition.Title);
				writer.WriteString("runsIn", definition.RunsIn == CommandLocation.Web ? "web" : "core");
				if (definition.Category is not null) {
					writer.WriteString("category", definition.Category);
				}

				writer.WriteString("description", definition.Description);
				writer.WriteStartArray("aliases");
				foreach (string alias in definition.Aliases) {
					writer.WriteStringValue(alias);
				}

				writer.WriteEndArray();
				writer.WriteBoolean("showInPalette", definition.ShowInPalette);
				if (definition.When is not null) {
					writer.WriteString("when", definition.When);
				}

				if (definition.ArgsSchemaJson is not null) {
					writer.WritePropertyName("argsSchema");
					writer.WriteRawValue(definition.ArgsSchemaJson);
				}

				writer.WriteStartArray("keys");
				foreach (var binding in bindings) {
					if (string.Equals(binding.Command, definition.Id, StringComparison.Ordinal)) {
						writer.WriteStringValue(binding.Key);
					}
				}

				writer.WriteEndArray();
				writer.WriteEndObject();
			}

			writer.WriteEndArray();
		}

		return Encoding.UTF8.GetString(stream.ToArray());
	}

	/// <summary>Builds the flat keybindings array the web resolves keydowns against: <c>{key, command, args?, when?}</c>.</summary>
	public static string BuildKeybindingsArrayJson(IReadOnlyList<ResolvedKeybinding> bindings) {
		ArgumentNullException.ThrowIfNull(bindings);

		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream)) {
			writer.WriteStartArray();
			foreach (var binding in bindings) {
				writer.WriteStartObject();
				writer.WriteString("key", binding.Key);
				writer.WriteString("command", binding.Command);
				if (binding.ArgsJson is not null) {
					writer.WritePropertyName("args");
					writer.WriteRawValue(binding.ArgsJson);
				}

				if (binding.When is not null) {
					writer.WriteString("when", binding.When);
				}

				// Emit `global` only when set (absent ⇒ false), keeping the array lean. The web resolver
				// skips global bindings — the host registers them with the OS instead.
				if (binding.Global) {
					writer.WriteBoolean("global", true);
				}

				writer.WriteEndObject();
			}

			writer.WriteEndArray();
		}

		return Encoding.UTF8.GetString(stream.ToArray());
	}
}
