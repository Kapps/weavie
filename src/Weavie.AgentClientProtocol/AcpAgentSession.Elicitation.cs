using System.Globalization;
using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	private object RequestInput(AcpClientRequest request, AcpClientRequestState state) {
		string mode = RequiredString(request.Parameters, "mode", "elicitation request");
		if (mode == "url") {
			string elicitationId = RequiredString(request.Parameters, "elicitationId", "URL elicitation");
			string url = RequireHttpUrl(RequiredString(request.Parameters, "url", "URL elicitation"));
			if (!_urlElicitations.TryAdd(elicitationId, request.Id)) {
				throw new AcpProtocolException($"ACP repeated outstanding URL elicitation id '{elicitationId}'.");
			}
			var data = JsonSerializer.SerializeToElement(Array.Empty<object>());
			var urlPending = new AcpPendingRequest(request, "url", data, SessionId(), TurnId());
			if (!_pendingRequests.TryAdd(request.Id, urlPending)) {
				_urlElicitations.TryRemove(elicitationId, out _);
				throw new AcpProtocolException($"ACP request id '{request.Id}' is already pending.");
			}
			try {
				PublishInputRequest(state, urlPending, () => new AgentPaneMessage {
					Type = "input-requested",
					ProviderId = _definition.Id,
					ItemId = $"request:{request.Id}",
					RequestId = request.Id,
					ItemType = "url",
					Summary = OptionalString(request.Parameters, "message") ?? "Open this link to continue",
					ResourceUri = url,
					Actions = [new AgentActionOption { Id = "accept", Label = "Open link", Kind = "open_url" }],
					Status = "pending",
				});
			} catch {
				_urlElicitations.TryRemove(elicitationId, out _);
				throw;
			}
			return DeferredClientResponse;
		}
		if (mode != "form" || !request.Parameters.TryGetProperty("requestedSchema", out var schema)) {
			throw new AcpProtocolException($"Unsupported ACP elicitation mode '{mode}'.");
		}
		var questions = ReadQuestions(schema, OptionalString(request.Parameters, "message"));
		var pending = new AcpPendingRequest(request, "input", schema.Clone(), SessionId(), TurnId());
		if (!_pendingRequests.TryAdd(request.Id, pending)) {
			throw new AcpProtocolException($"ACP request id '{request.Id}' is already pending.");
		}
		PublishInputRequest(state, pending, () => new AgentPaneMessage {
			Type = "input-requested",
			ProviderId = _definition.Id,
			ItemId = $"request:{request.Id}",
			RequestId = request.Id,
			ItemType = "elicitation",
			Summary = OptionalString(request.Parameters, "message") ?? "Input requested",
			Questions = questions,
			Status = "pending",
		});
		return DeferredClientResponse;
	}

	private void PublishInputRequest(
		AcpClientRequestState state,
		AcpPendingRequest pending,
		Func<AgentPaneMessage> createMessage) {
		if (state.PublishDeferred(() => {
			Observe(new AgentInputRequested());
			Observe(new AgentInputResolved(RequiresUserInput: true));
			// The pane keys an item by (threadId, turnId, itemId), and the resolution reads its identity off this
			// same record -- so stamping it here is what keeps the two from ever disagreeing.
			Emit(createMessage() with { ThreadId = pending.ThreadId, TurnId = pending.TurnId });
		})) return;
		_pendingRequests.TryRemove(pending.Request.Id, out _);
		state.Token.ThrowIfCancellationRequested();
	}

	private static IReadOnlyList<AgentInputQuestion> ReadQuestions(JsonElement schema, string? message) {
		var properties = ReadObjectSchemaProperties(schema);
		var required = ReadRequiredProperties(schema);
		var propertyNames = properties.Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
		string[] unknownRequired = [.. required.Except(propertyNames, StringComparer.Ordinal)];
		if (unknownRequired.Length > 0) {
			throw new AcpProtocolException(
				"ACP elicitation required contains unknown properties: " + string.Join(", ", unknownRequired));
		}
		var result = new List<AgentInputQuestion>();
		foreach (var property in properties) {
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
				AllowsOther = false,
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

	private static string RequireHttpUrl(string value) {
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

	private static Dictionary<string, object> BuildElicitationContent(
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
		var content = new Dictionary<string, object>(StringComparer.Ordinal);
		foreach (var property in properties) {
			string kind = RequiredString(property.Value, "type", $"elicitation property '{property.Name}'");
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

	private static object ConvertElicitationValue(
		string name,
		JsonElement schema,
		string kind,
		IReadOnlyList<string> values) {
		if (kind == "array") {
			ValidateSelection(name, schema, values);
			return values;
		}
		if (values.Count != 1) {
			throw new AcpProtocolException($"'{name}' accepts exactly one value.");
		}
		string value = values[0];
		return kind switch {
			"string" => ValidateString(name, schema, value),
			"boolean" when bool.TryParse(value, out bool result) => result,
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

	private static object ValidateNumber(string name, JsonElement schema, double value) {
		double? minimum = ReadOptionalDouble(schema, "minimum");
		double? maximum = ReadOptionalDouble(schema, "maximum");
		if (minimum is not null && value < minimum || maximum is not null && value > maximum) {
			throw new AcpProtocolException($"'{name}' is outside its allowed range.");
		}
		return value;
	}

	private static object ValidateNumber(string name, JsonElement schema, long value) {
		double? minimum = ReadOptionalDouble(schema, "minimum");
		double? maximum = ReadOptionalDouble(schema, "maximum");
		if (minimum is not null && value < minimum || maximum is not null && value > maximum) {
			throw new AcpProtocolException($"'{name}' is outside its allowed range.");
		}
		return value;
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
