using System.Globalization;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Weavie.Core.Agents;
using static Weavie.AgentClientProtocol.AcpJson;

namespace Weavie.AgentClientProtocol;

/// <summary>Reads ACP elicitation schemas and validates the user's answers against them.</summary>
internal static class AcpElicitationSchema {
	public static IReadOnlyList<AgentInputQuestion> ReadQuestions(JsonElement schema, string? message) {
		var properties = ReadObjectSchemaProperties(schema);
		var required = ReadRequiredProperties(schema);
		var propertyNames = properties.Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
		string[] unknownRequired = [.. required.Except(propertyNames, StringComparer.Ordinal)];
		if (unknownRequired.Length > 0) {
			throw new AcpProtocolException(
				"ACP elicitation required contains unknown properties: " + string.Join(", ", unknownRequired));
		}
		var companions = CustomAnswerCompanions(properties);
		var result = new List<AgentInputQuestion>();
		foreach (var property in properties) {
			if (companions.ContainsValue(property.Name)) continue;
			var value = property.Value;
			string kind = RequiredString(value, "type", $"elicitation property '{property.Name}'");
			if (kind is not ("string" or "number" or "integer" or "boolean" or "array")) {
				throw new AcpProtocolException($"Unsupported ACP elicitation property type '{kind}'.");
			}
			string title = OptionalString(value, "title") ?? property.Name;
			string? format = OptionalString(value, "format");
			if (format == "password") {
				throw new AcpProtocolException(
					"ACP password forms are not supported; use a secure HTTPS URL elicitation instead.");
			}
			if (format is not null && (kind != "string" || format is not ("email" or "uri" or "date" or "date-time"))) {
				throw new AcpProtocolException($"Unsupported ACP elicitation format '{format}'.");
			}
			if (OptionalString(value, "pattern") is { } pattern) ValidatePattern(pattern);
			result.Add(new AgentInputQuestion {
				Id = property.Name,
				Header = title,
				Question = OptionalString(value, "description") ?? message ?? title,
				AllowsOther = companions.ContainsKey(property.Name),
				Kind = kind,
				Required = required.Contains(property.Name),
				Format = format,
				InitialValues = ReadDefaultValues(value, kind),
				Minimum = ReadOptionalDouble(value, "minimum"),
				Maximum = ReadOptionalDouble(value, "maximum"),
				MinimumLength = ReadOptionalNonNegativeInt(
					value,
					kind == "array" ? "minItems" : "minLength"),
				MaximumLength = ReadOptionalNonNegativeInt(
					value,
					kind == "array" ? "maxItems" : "maxLength"),
				Pattern = OptionalString(value, "pattern"),
				Options = ReadOptions(value, kind),
			});
		}
		return result;
	}

	// An AIR custom-answer property is the free-text companion of a choice question: the question offers Other,
	// and the companion carries what Other cannot. It names its question, or follows it.
	private static Dictionary<string, string> CustomAnswerCompanions(JsonProperty[] properties) {
		var companions = new Dictionary<string, string>(StringComparer.Ordinal);
		for (int index = 0; index < properties.Length; index++) {
			var marker = Air(properties[index].Value, "customAnswer");
			if (marker.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False) continue;
			string question = (marker.ValueKind == JsonValueKind.Object ? OptionalString(marker, "questionId") : null)
				?? (index > 0 ? properties[index - 1].Name : throw new AcpProtocolException("An ACP custom answer names no question."));
			if (!properties.Any(property => property.Name == question) || !companions.TryAdd(question, properties[index].Name)) {
				throw new AcpProtocolException($"The ACP custom answer for '{question}' has no single question.");
			}
		}
		return companions;
	}

	public static string RequireHttpUrl(string value) {
		if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
			|| uri.Scheme is not ("http" or "https")
			|| string.IsNullOrEmpty(uri.Host)) {
			throw new AcpProtocolException("ACP URL elicitation requires an absolute HTTP or HTTPS URL.");
		}
		return value;
	}

	private static IReadOnlyList<AgentInputOption> ReadOptions(JsonElement property, string kind) {
		var choices = property;
		string titledProperty = "oneOf";
		if (kind == "array") {
			if (!property.TryGetProperty("items", out choices) || choices.ValueKind != JsonValueKind.Object) {
				throw new AcpProtocolException("An ACP array elicitation property is missing items.");
			}
			if (choices.TryGetProperty("type", out var itemType)) {
				if (itemType.ValueKind != JsonValueKind.String || itemType.GetString() != "string") {
					throw new AcpProtocolException("ACP array elicitation items must be strings.");
				}
			} else if (!choices.TryGetProperty("anyOf", out var anyOf) || anyOf.ValueKind != JsonValueKind.Array) {
				throw new AcpProtocolException("ACP array elicitation items must be strings.");
			}
			titledProperty = "anyOf";
		}
		if (choices.TryGetProperty("enum", out var values)) {
			if (values.ValueKind == JsonValueKind.Null) return ReadTitledOptions(choices, titledProperty);
			if (values.ValueKind != JsonValueKind.Array) {
				throw new AcpProtocolException("ACP elicitation enum must be an array.");
			}
			return [.. values.EnumerateArray().Select(value => {
			if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text) {
				throw new AcpProtocolException("ACP elicitation enum values must be strings.");
			}
			return new AgentInputOption { Value = text, Label = text, Description = string.Empty };
		})];
		}
		return ReadTitledOptions(choices, titledProperty);
	}

	private static IReadOnlyList<AgentInputOption> ReadTitledOptions(JsonElement choices, string property) {
		if (choices.TryGetProperty(property, out var values)) {
			if (values.ValueKind == JsonValueKind.Null) return [];
			if (values.ValueKind != JsonValueKind.Array) {
				throw new AcpProtocolException($"ACP elicitation {property} must be an array.");
			}
			return [.. values.EnumerateArray().Select(value => new AgentInputOption {
			Value = RequiredString(value, "const", "elicitation option"),
			Label = RequiredString(value, "title", "elicitation option"),
			Description = OptionalString(value, "description") ?? string.Empty,
		})];
		}
		return [];
	}

	private static IReadOnlyList<string> ReadDefaultValues(JsonElement property, string kind) {
		if (!property.TryGetProperty("default", out var value) || value.ValueKind == JsonValueKind.Null) {
			return [];
		}
		if (kind == "array") {
			if (value.ValueKind != JsonValueKind.Array) {
				throw new AcpProtocolException("An ACP array elicitation default must be an array.");
			}
			return [.. value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
			? item.GetString() ?? string.Empty
			: throw new AcpProtocolException("ACP array elicitation defaults must contain strings."))];
		}
		return kind switch {
			"string" when value.ValueKind == JsonValueKind.String => [value.GetString() ?? string.Empty],
			"boolean" when value.ValueKind is JsonValueKind.True or JsonValueKind.False =>
				[value.GetBoolean().ToString().ToLowerInvariant()],
			"number" or "integer" when value.ValueKind == JsonValueKind.Number => [value.GetRawText()],
			_ => throw new AcpProtocolException($"The ACP {kind} elicitation default has the wrong type."),
		};
	}

	private static HashSet<string> ReadRequiredProperties(JsonElement schema) {
		if (!schema.TryGetProperty("required", out var required) || required.ValueKind == JsonValueKind.Null) {
			return new HashSet<string>(StringComparer.Ordinal);
		}
		if (required.ValueKind != JsonValueKind.Array) {
			throw new AcpProtocolException("ACP elicitation required must be an array.");
		}
		return new HashSet<string>(required.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
			? value.GetString() ?? string.Empty
			: throw new AcpProtocolException("ACP elicitation required entries must be strings.")), StringComparer.Ordinal);
	}

	public static JsonObject BuildElicitationContent(
		JsonElement schema,
		IReadOnlyDictionary<string, IReadOnlyList<string>> answers) {
		var properties = ReadObjectSchemaProperties(schema);
		var required = ReadRequiredProperties(schema);
		string[] unknown = [.. answers.Keys.Except(
			properties.Select(property => property.Name),
			StringComparer.Ordinal)];
		if (unknown.Length > 0) {
			throw new AcpProtocolException("ACP elicitation answers contain unknown properties: "
				+ string.Join(", ", unknown));
		}
		var companions = CustomAnswerCompanions(properties);
		answers = FoldCustomAnswers(properties, companions, answers);
		var content = new JsonObject();
		foreach (var property in properties) {
			string kind = RequiredString(property.Value, "type", $"elicitation property '{property.Name}'");
			if (kind != "array" && answers.TryGetValue(property.Name, out var single) && single.Count == 1
				&& companions.ContainsKey(property.Name)
				&& ReadOptions(property.Value, kind).All(option => option.Value != single[0])) {
				// A typed single choice is the answer itself.
				content.Add(property.Name, single[0]);
				continue;
			}
			if (!answers.TryGetValue(property.Name, out var values)
				|| values.Count == 0 && kind != "array") {
				if (required.Contains(property.Name)) {
					throw new AcpProtocolException($"'{property.Name}' requires an answer.");
				}
				continue;
			}
			content.Add(property.Name, ConvertElicitationValue(property.Name, property.Value, kind, values));
		}
		return content;
	}

	// Typed text beside a multiple choice's picks belongs in its custom-answer companion.
	private static IReadOnlyDictionary<string, IReadOnlyList<string>> FoldCustomAnswers(
		JsonProperty[] properties, Dictionary<string, string> companions, IReadOnlyDictionary<string, IReadOnlyList<string>> answers) {
		var folded = new Dictionary<string, IReadOnlyList<string>>(answers, StringComparer.Ordinal);
		foreach (var (question, companion) in companions) {
			var schema = properties.First(property => property.Name == question).Value;
			if (RequiredString(schema, "type", $"elicitation property '{question}'") != "array" || !answers.TryGetValue(question, out var values)) continue;
			var options = ReadOptions(schema, "array");
			string[] typed = [.. values.Where(value => options.All(option => option.Value != value))];
			if (typed.Length == 0) continue;
			folded[question] = [.. values.Except(typed, StringComparer.Ordinal)];
			folded[companion] = [string.Join(", ", typed)];
		}
		return folded;
	}

	private static JsonProperty[] ReadObjectSchemaProperties(JsonElement schema) {
		if (schema.ValueKind != JsonValueKind.Object) {
			throw new AcpProtocolException("The ACP elicitation schema must be an object.");
		}
		if (schema.TryGetProperty("type", out var type)
			&& (type.ValueKind != JsonValueKind.String || type.GetString() != "object")) {
			throw new AcpProtocolException("The ACP elicitation schema type must be 'object'.");
		}
		if (!schema.TryGetProperty("properties", out var properties)) return [];
		if (properties.ValueKind != JsonValueKind.Object) {
			throw new AcpProtocolException("ACP elicitation properties must be an object.");
		}
		return [.. properties.EnumerateObject()];
	}

	private static JsonNode ConvertElicitationValue(
		string name,
		JsonElement schema,
		string kind,
		IReadOnlyList<string> values) {
		if (kind == "array") {
			ValidateSelection(name, schema, values);
			return new JsonArray([.. values.Select(value => JsonValue.Create(value))]);
		}
		if (values.Count != 1) {
			throw new AcpProtocolException($"'{name}' accepts exactly one value.");
		}
		string value = values[0];
		return kind switch {
			"string" => JsonValue.Create(ValidateString(name, schema, value)),
			"boolean" when bool.TryParse(value, out bool result) => JsonValue.Create(result),
			"integer" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) =>
				ValidateNumber(name, schema, result),
			"number" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
				&& double.IsFinite(result) => ValidateNumber(name, schema, result),
			"boolean" or "integer" or "number" =>
				throw new AcpProtocolException($"'{name}' is not a valid {kind} value."),
			_ => throw new AcpProtocolException($"Unsupported ACP elicitation property type '{kind}'."),
		};
	}

	private static string ValidateString(string name, JsonElement schema, string value) {
		int? minimum = ReadOptionalNonNegativeInt(schema, "minLength");
		int? maximum = ReadOptionalNonNegativeInt(schema, "maxLength");
		if (minimum is not null && value.Length < minimum || maximum is not null && value.Length > maximum) {
			throw new AcpProtocolException($"'{name}' does not meet its length constraint.");
		}
		if (OptionalString(schema, "pattern") is { } pattern
			&& !Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant)) {
			throw new AcpProtocolException($"'{name}' does not match its required pattern.");
		}
		ValidateStringFormat(name, OptionalString(schema, "format"), value);
		ValidateOption(name, ReadOptions(schema, "string"), value);
		return value;
	}

	private static void ValidatePattern(string pattern) {
		try {
			_ = new Regex(pattern, RegexOptions.CultureInvariant);
		} catch (ArgumentException ex) {
			throw new AcpProtocolException("ACP elicitation pattern is not a valid regular expression.", ex);
		}
	}

	private static void ValidateStringFormat(string name, string? format, string value) {
		bool valid = format switch {
			null => true,
			"email" => MailAddress.TryCreate(value, out var address)
				&& string.Equals(address.Address, value, StringComparison.OrdinalIgnoreCase),
			"uri" => Uri.TryCreate(value, UriKind.Absolute, out _),
			"date" => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
			"date-time" => value.Contains('T')
				&& Regex.IsMatch(value, "(?:Z|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.CultureInvariant)
				&& DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
			_ => false,
		};
		if (!valid) throw new AcpProtocolException($"'{name}' is not a valid {format} value.");
	}

	private static JsonNode ValidateNumber(string name, JsonElement schema, double value) {
		double? minimum = ReadOptionalDouble(schema, "minimum");
		double? maximum = ReadOptionalDouble(schema, "maximum");
		if (minimum is not null && value < minimum || maximum is not null && value > maximum) {
			throw new AcpProtocolException($"'{name}' is outside its allowed range.");
		}
		return JsonValue.Create(value);
	}

	private static JsonNode ValidateNumber(string name, JsonElement schema, long value) {
		double? minimum = ReadOptionalDouble(schema, "minimum");
		double? maximum = ReadOptionalDouble(schema, "maximum");
		if (minimum is not null && value < minimum || maximum is not null && value > maximum) {
			throw new AcpProtocolException($"'{name}' is outside its allowed range.");
		}
		return JsonValue.Create(value);
	}

	private static void ValidateSelection(string name, JsonElement schema, IReadOnlyList<string> values) {
		int? minimum = ReadOptionalNonNegativeInt(schema, "minItems");
		int? maximum = ReadOptionalNonNegativeInt(schema, "maxItems");
		if (minimum is not null && values.Count < minimum || maximum is not null && values.Count > maximum) {
			throw new AcpProtocolException($"'{name}' does not meet its selection count constraint.");
		}
		if (schema.TryGetProperty("uniqueItems", out var unique)) {
			if (unique.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) {
				throw new AcpProtocolException($"'{name}' uniqueItems must be boolean.");
			}
			if (unique.ValueKind == JsonValueKind.True
				&& values.Distinct(StringComparer.Ordinal).Count() != values.Count) {
				throw new AcpProtocolException($"'{name}' requires unique values.");
			}
		}
		foreach (string value in values) {
			ValidateOption(name, ReadOptions(schema, "array"), value);
		}
	}

	private static void ValidateOption(string name, IReadOnlyList<AgentInputOption> options, string value) {
		if (options.Count > 0 && options.All(option => !string.Equals(option.Value, value, StringComparison.Ordinal))) {
			throw new AcpProtocolException($"'{value}' was not advertised for '{name}'.");
		}
	}
}
