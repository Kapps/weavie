using System.Text.Json;
using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

internal static class AcpConfigurationOptions {
	public static IReadOnlyList<AgentControlAxis> ReadIfPresent(JsonElement owner) {
		if (!owner.TryGetProperty("configOptions", out var options) || options.ValueKind == JsonValueKind.Null) {
			return [];
		}
		if (options.ValueKind != JsonValueKind.Array) {
			throw new AcpProtocolException("ACP configOptions must be an array when present.");
		}
		return Read(options);
	}

	public static IReadOnlyList<AgentControlAxis> ReadRequired(JsonElement owner, string missingMessage) {
		if (!owner.TryGetProperty("configOptions", out var options) || options.ValueKind != JsonValueKind.Array) {
			throw new AcpProtocolException(missingMessage);
		}
		return Read(options);
	}

	public static object SetParameters(string sessionId, AgentControlAxis control, string value) {
		var parameters = JsonSerializer.SerializeToNode(SetParameters(control, value))!;
		parameters["sessionId"] = System.Text.Json.Nodes.JsonValue.Create(sessionId);
		return parameters;
	}

	public static object SetParameters(AgentControlAxis control, string value) {
		if (control.Options.All(option => option.Id != value)) {
			throw new AcpProtocolException(
				$"ACP no longer advertises '{value}' for the '{control.Id}' control.");
		}
		return control.Kind switch {
			"select" => new { configId = control.Id, value },
			"boolean" when bool.TryParse(value, out bool boolean) =>
				new { configId = control.Id, type = "boolean", value = boolean },
			"boolean" => throw new AcpProtocolException($"'{value}' is not a boolean ACP configuration value."),
			_ => throw new AcpProtocolException($"Unsupported ACP config option type '{control.Kind}'."),
		};
	}

	private static IReadOnlyList<AgentControlAxis> Read(JsonElement options) {
		var result = new List<AgentControlAxis>();
		foreach (var option in options.EnumerateArray()) {
			string id = RequiredString(option, "id", "session config option");
			if (result.Any(control => control.Id == id)) {
				throw new AcpProtocolException($"ACP repeated session config option '{id}'.");
			}
			string kind = RequiredString(option, "type", "session config option");
			string value;
			IReadOnlyList<AgentControlOption> values;
			if (kind == "boolean") {
				if (!option.TryGetProperty("currentValue", out var current)
					|| current.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) {
					throw new AcpProtocolException($"Boolean ACP config option '{id}' has no boolean currentValue.");
				}
				value = current.GetBoolean().ToString().ToLowerInvariant();
				values = [
					new AgentControlOption { Id = "true", Label = "On" },
					new AgentControlOption { Id = "false", Label = "Off" },
				];
			} else if (kind == "select") {
				value = RequiredString(option, "currentValue", "session config option");
				if (!option.TryGetProperty("options", out var choices) || choices.ValueKind != JsonValueKind.Array) {
					throw new AcpProtocolException($"Select ACP config option '{id}' has no options.");
				}
				values = ReadSelectOptions(id, choices);
				if (values.All(choice => choice.Id != value)) {
					throw new AcpProtocolException(
						$"Select ACP config option '{id}' has unadvertised currentValue '{value}'.");
				}
			} else {
				throw new AcpProtocolException($"Unsupported ACP config option type '{kind}'.");
			}
			result.Add(new AgentControlAxis {
				Id = id,
				Label = RequiredString(option, "name", "session config option"),
				Description = OptionalString(option, "description"),
				Category = OptionalString(option, "category"),
				Kind = kind,
				Value = value,
				ValueLabel = values.FirstOrDefault(choice => choice.Id == value)?.Label ?? value,
				Options = values,
			});
		}
		return result;
	}

	private static IReadOnlyList<AgentControlOption> ReadSelectOptions(string configId, JsonElement choices) {
		var entries = choices.EnumerateArray().ToArray();
		bool grouped = entries.Length > 0 && entries.All(choice => choice.TryGetProperty("options", out _));
		if (!grouped && entries.Any(choice => choice.TryGetProperty("options", out _))) {
			throw new AcpProtocolException($"Select ACP config option '{configId}' mixes grouped and flat values.");
		}
		var values = grouped
			? entries.SelectMany(group => {
				RequiredString(group, "group", "session config group");
				string name = RequiredString(group, "name", "session config group");
				if (!group.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array) {
					throw new AcpProtocolException($"Select ACP config group '{name}' has no options.");
				}
				return options.EnumerateArray().Select(choice => SelectOption(choice, name));
			})
			: entries.Select(choice => SelectOption(choice, null));
		var result = values.ToArray();
		if (result.Select(option => option.Id).Distinct(StringComparer.Ordinal).Count() != result.Length) {
			throw new AcpProtocolException($"Select ACP config option '{configId}' repeats a value id.");
		}
		return result;
	}

	private static AgentControlOption SelectOption(JsonElement choice, string? group) => new() {
		Id = RequiredString(choice, "value", "session config value"),
		Label = RequiredString(choice, "name", "session config value"),
		Description = OptionalString(choice, "description"),
		Group = group,
	};

	private static string RequiredString(JsonElement value, string property, string source) =>
		value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String
			&& result.GetString() is { Length: > 0 } text
				? text
				: throw new AcpProtocolException($"The {source} is missing '{property}'.");

	private static string? OptionalString(JsonElement value, string property) =>
		value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String
			? result.GetString()
			: null;
}
