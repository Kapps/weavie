using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Weavie.Core.Workflows;

/// <summary>One problem in a workflow file, located by path and position.</summary>
public sealed record WorkflowDiagnostic {
	/// <summary>The node path (<c>stages[2].do</c>), or empty for the whole file.</summary>
	public required string Path { get; init; }

	/// <summary>Where the node starts.</summary>
	public required WorkflowPosition Position { get; init; }

	/// <summary>What is wrong.</summary>
	public required string Message { get; init; }

	/// <inheritdoc/>
	public override string ToString() =>
		$"{Message}{(Path.Length == 0 ? "" : $" at {Path}")} (line {Position.Line}, column {Position.Column})";
}

/// <summary>Collects diagnostics against one document's positions.</summary>
internal sealed class WorkflowCheck(IReadOnlyDictionary<string, WorkflowPosition> positions) {
	private readonly List<WorkflowDiagnostic> _diagnostics = [];

	public IReadOnlyList<WorkflowDiagnostic> Diagnostics => _diagnostics;

	public void Add(string path, string message) =>
		_diagnostics.Add(new WorkflowDiagnostic { Path = path, Position = Locate(path), Message = message });

	private WorkflowPosition Locate(string path) {
		for (string p = path; ; p = Parent(p)) {
			if (positions.TryGetValue(p, out var position)) {
				return position;
			}

			if (p.Length == 0) {
				return new WorkflowPosition(1, 1);
			}
		}
	}

	private static string Parent(string path) => path[..Math.Max(0, path.LastIndexOfAny(['.', '[']))];

	public static string Child(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";
}

/// <summary>A node's allowed shape: validates a YAML node and describes itself as JSON Schema.</summary>
internal abstract class WorkflowShape(string description) {
	public abstract void Check(JsonNode? node, string path, WorkflowCheck check);

	public JsonObject Schema() {
		var schema = SchemaCore();
		if (description.Length > 0) {
			schema["description"] = description;
		}

		return schema;
	}

	protected abstract JsonObject SchemaCore();

	protected static bool IsText(JsonNode? node, out string text) {
		bool isText = node is JsonValue value && value.GetValueKind() == JsonValueKind.String;
		text = isText ? node!.GetValue<string>() : "";
		return isText;
	}
}

internal sealed class TextShape(string description, Regex? pattern, string patternHint) : WorkflowShape(description) {
	public TextShape(string description) : this(description, null, "") { }

	public override void Check(JsonNode? node, string path, WorkflowCheck check) {
		if (!IsText(node, out string text)) {
			check.Add(path, "expected text");
		} else if (pattern is not null && !pattern.IsMatch(text)) {
			check.Add(path, $"\"{text}\" must be {patternHint}");
		}
	}

	protected override JsonObject SchemaCore() {
		var schema = new JsonObject { ["type"] = "string" };
		if (pattern is not null) {
			schema["pattern"] = pattern.ToString();
		}

		return schema;
	}
}

internal sealed class IntShape(string description, int minimum) : WorkflowShape(description) {
	public override void Check(JsonNode? node, string path, WorkflowCheck check) {
		if (!IsText(node, out string text) || !int.TryParse(text, out int value) || value < minimum) {
			check.Add(path, $"expected a whole number of at least {minimum}");
		}
	}

	protected override JsonObject SchemaCore() => new() { ["type"] = "integer", ["minimum"] = minimum };
}

internal sealed class EnumShape(string description, IReadOnlyList<string> values) : WorkflowShape(description) {
	public override void Check(JsonNode? node, string path, WorkflowCheck check) {
		if (!IsText(node, out string text) || !values.Contains(text)) {
			check.Add(path, $"expected one of {string.Join(", ", values)}");
		}
	}

	protected override JsonObject SchemaCore() {
		var options = new JsonArray();
		foreach (string value in values) {
			options.Add((JsonNode)JsonValue.Create(value));
		}

		return new JsonObject { ["enum"] = options };
	}
}

internal sealed record WorkflowField(string Name, WorkflowShape Shape, bool Required);

internal sealed class ObjectShape(string description, IReadOnlyList<WorkflowField> fields) : WorkflowShape(description) {
	public override void Check(JsonNode? node, string path, WorkflowCheck check) {
		if (node is not JsonObject obj) {
			check.Add(path, "expected a mapping");
			return;
		}

		foreach (var (key, value) in obj) {
			var field = fields.FirstOrDefault(f => f.Name == key);
			if (field is null) {
				check.Add(WorkflowCheck.Child(path, key), $"unknown key \"{key}\"");
			} else {
				field.Shape.Check(value, WorkflowCheck.Child(path, key), check);
			}
		}

		foreach (var field in fields.Where(f => f.Required && !obj.ContainsKey(f.Name))) {
			check.Add(path, $"missing required key \"{field.Name}\"");
		}
	}

	protected override JsonObject SchemaCore() {
		var properties = new JsonObject();
		var required = new JsonArray();
		foreach (var field in fields) {
			properties[field.Name] = field.Shape.Schema();
			if (field.Required) {
				required.Add((JsonNode)JsonValue.Create(field.Name));
			}
		}

		return new JsonObject {
			["type"] = "object",
			["properties"] = properties,
			["required"] = required,
			["additionalProperties"] = false,
		};
	}
}

internal sealed class MapShape(string description, Regex keyPattern, string keyHint, WorkflowShape value) : WorkflowShape(description) {
	public override void Check(JsonNode? node, string path, WorkflowCheck check) {
		if (node is not JsonObject obj) {
			check.Add(path, "expected a mapping");
			return;
		}

		foreach (var (key, child) in obj) {
			string childPath = WorkflowCheck.Child(path, key);
			if (!keyPattern.IsMatch(key)) {
				check.Add(childPath, $"key \"{key}\" must be {keyHint}");
			}

			value.Check(child, childPath, check);
		}
	}

	protected override JsonObject SchemaCore() => new() {
		["type"] = "object",
		["propertyNames"] = new JsonObject { ["pattern"] = keyPattern.ToString() },
		["additionalProperties"] = value.Schema(),
	};
}

internal sealed class ListShape(string description, WorkflowShape item) : WorkflowShape(description) {
	public override void Check(JsonNode? node, string path, WorkflowCheck check) {
		if (node is not JsonArray array || array.Count == 0) {
			check.Add(path, "expected a non-empty list");
			return;
		}

		for (int i = 0; i < array.Count; i++) {
			item.Check(array[i], $"{path}[{i}]", check);
		}
	}

	protected override JsonObject SchemaCore() =>
		new() { ["type"] = "array", ["items"] = item.Schema(), ["minItems"] = 1 };
}

/// <summary>Text or a mapping, told apart by the node's kind.</summary>
internal sealed class TextOrObjectShape(string description, WorkflowShape text, WorkflowShape mapping) : WorkflowShape(description) {
	public override void Check(JsonNode? node, string path, WorkflowCheck check) {
		if (node is JsonObject) {
			mapping.Check(node, path, check);
		} else {
			text.Check(node, path, check);
		}
	}

	protected override JsonObject SchemaCore() => new() { ["oneOf"] = new JsonArray(text.Schema(), mapping.Schema()) };
}
