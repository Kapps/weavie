using System.Text.Json;
using Weavie.Core.Json;

namespace Weavie.Core.Commands;

/// <summary>
/// Serializes the command catalog and resolved keybindings to JSON — the one source for the MCP
/// <c>listCommands</c>/<c>describeCommand</c> tools, the web injection, and the live <c>commands</c> push.
/// </summary>
public static class CommandCatalog {
	/// <summary>
	/// Builds the commands array: each command's metadata plus the keys it is currently bound to (from
	/// <paramref name="bindings"/>).
	/// </summary>
	public static string BuildCommandsArrayJson(
		IReadOnlyList<CommandDefinition> definitions,
		IReadOnlyList<ResolvedKeybinding> bindings) {
		ArgumentNullException.ThrowIfNull(definitions);
		ArgumentNullException.ThrowIfNull(bindings);

		return JsonWrite.ToText(writer => {
			writer.WriteStartArray();
			foreach (var definition in definitions) {
				WriteCommand(writer, definition, bindings);
			}

			writer.WriteEndArray();
		});
	}

	/// <summary>Builds one command's full entry, as it appears in <see cref="BuildCommandsArrayJson"/>.</summary>
	public static string BuildCommandJson(CommandDefinition definition, IReadOnlyList<ResolvedKeybinding> bindings) {
		ArgumentNullException.ThrowIfNull(definition);
		ArgumentNullException.ThrowIfNull(bindings);

		return JsonWrite.ToText(writer => WriteCommand(writer, definition, bindings));
	}

	/// <summary>
	/// Builds the <c>listCommands</c> index: per command its id, title, category, bound keys, and argument
	/// names — enough to pick a command, with the rest left to <see cref="BuildCommandJson"/>.
	/// </summary>
	public static string BuildCommandIndexJson(
		IReadOnlyList<CommandDefinition> definitions,
		IReadOnlyList<ResolvedKeybinding> bindings) {
		ArgumentNullException.ThrowIfNull(definitions);
		ArgumentNullException.ThrowIfNull(bindings);

		return JsonWrite.Object(writer => {
			writer.WriteStartArray("commands");
			foreach (var definition in definitions) {
				writer.WriteStartObject();
				writer.WriteString("id", definition.Id);
				writer.WriteString("title", definition.Title);
				if (definition.Category is not null) {
					writer.WriteString("category", definition.Category);
				}

				string[] keys = [.. KeysOf(definition, bindings)];
				if (keys.Length > 0) {
					WriteStrings(writer, "keys", keys);
				}

				if (definition.ArgsSchemaJson is not null) {
					using var schema = JsonDocument.Parse(definition.ArgsSchemaJson);
					WriteStrings(writer, "args", schema.RootElement.EnumerateObject().Select(arg => arg.Name));
				}

				writer.WriteEndObject();
			}

			writer.WriteEndArray();
		});
	}

	private static void WriteCommand(
		Utf8JsonWriter writer, CommandDefinition definition, IReadOnlyList<ResolvedKeybinding> bindings) {
		writer.WriteStartObject();
		writer.WriteString("id", definition.Id);
		writer.WriteString("title", definition.Title);
		writer.WriteString("runsIn", definition.RunsIn == CommandLocation.Web ? "web" : "core");
		writer.WriteString("owner", definition.Owner == CommandOwner.Client ? "client" : "backend");
		writer.WriteString("executionLane", definition.ExecutionLane);
		writer.WriteString("scope", definition.Scope == CommandScope.Host ? "host" : "session");
		if (definition.Category is not null) {
			writer.WriteString("category", definition.Category);
		}

		writer.WriteString("description", definition.Description);
		WriteStrings(writer, "aliases", definition.Aliases);
		writer.WriteBoolean("showInPalette", definition.ShowInPalette);
		if (definition.When is not null) {
			writer.WriteString("when", definition.When);
		}

		if (definition.ArgsSchemaJson is not null) {
			writer.WritePropertyName("argsSchema");
			writer.WriteRawValue(definition.ArgsSchemaJson);
		}

		WriteStrings(writer, "keys", KeysOf(definition, bindings));
		writer.WriteEndObject();
	}

	private static IEnumerable<string> KeysOf(CommandDefinition definition, IReadOnlyList<ResolvedKeybinding> bindings) =>
		bindings.Where(binding => string.Equals(binding.Command, definition.Id, StringComparison.Ordinal))
			.Select(binding => binding.Key);

	private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values) {
		writer.WriteStartArray(name);
		foreach (string value in values) {
			writer.WriteStringValue(value);
		}

		writer.WriteEndArray();
	}

	/// <summary>Builds the flat keybindings array the web resolves keydowns against.</summary>
	public static string BuildKeybindingsArrayJson(IReadOnlyList<ResolvedKeybinding> bindings) {
		ArgumentNullException.ThrowIfNull(bindings);

		return JsonWrite.ToText(writer => {
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

				if (binding.ActiveInModal) {
					writer.WriteBoolean("activeInModal", true);
				}

				// Emit `global` only when set (absent ⇒ false). The web resolver skips global bindings; the
				// host registers them with the OS instead.
				if (binding.Global) {
					writer.WriteBoolean("global", true);
				}

				writer.WriteEndObject();
			}

			writer.WriteEndArray();
		});
	}
}
