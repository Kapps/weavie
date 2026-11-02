using System.Text.Json.Nodes;

namespace Weavie.Core.Workflows;

/// <summary>A workflow file's definition, or the reasons it has none.</summary>
public sealed record WorkflowReadResult {
	/// <summary>The definition when the file is valid; otherwise <see langword="null"/>.</summary>
	public required WorkflowDefinition? Definition { get; init; }

	/// <summary>Every problem found; empty exactly when <see cref="Definition"/> is set.</summary>
	public required IReadOnlyList<WorkflowDiagnostic> Diagnostics { get; init; }
}

/// <summary>Reads workflow file text: YAML, then the schema's shape, then the meaning across fields.</summary>
public static class WorkflowReader {
	/// <summary>Reads <paramref name="text"/> from a file named <paramref name="fileId"/> (<c>quick-fix.yaml</c> → <c>quick-fix</c>).</summary>
	public static WorkflowReadResult Read(string text, string fileId) {
		YamlDocument document;
		try {
			document = YamlNodeReader.Read(text);
		} catch (WorkflowFormatException ex) {
			return Failed([new WorkflowDiagnostic { Path = "", Position = ex.Position, Message = ex.Message }]);
		}

		var check = new WorkflowCheck(document.Positions);
		WorkflowSchema.Root.Check(document.Root, "", check);
		if (check.Diagnostics.Count > 0) {
			return Failed(check.Diagnostics);
		}

		var root = document.Root!.AsObject();
		var definition = new WorkflowDefinition {
			Id = Text(root["id"]),
			Description = Text(root["description"]),
			Inputs = root["inputs"] is JsonObject inputs
				? [.. inputs.Select(input => new WorkflowInputSpec {
					Name = input.Key,
					Type = WorkflowValues.ParseType(Text(input.Value).TrimEnd('?')),
					Optional = Text(input.Value).EndsWith('?'),
				})]
				: [],
			Stages = [.. root["stages"]!.AsArray().Select((stage, i) => ReadStage(stage!.AsObject(), $"stages[{i}]", check))],
			SourceText = text,
		};
		WorkflowSemantics.Check(definition, fileId, check);
		return check.Diagnostics.Count > 0 ? Failed(check.Diagnostics) : new WorkflowReadResult { Definition = definition, Diagnostics = [] };
	}

	private static WorkflowStage ReadStage(JsonObject stage, string path, WorkflowCheck check) {
		var work = stage["do"]!.AsObject();
		return new WorkflowStage {
			Id = Text(stage["id"]),
			When = Template(stage["when"], $"{path}.when", check),
			Else = stage["else"] is null ? null : Text(stage["else"]),
			Do = new WorkflowWork {
				Prompt = Template(work["prompt"], $"{path}.do.prompt", check),
				Skill = work["skill"] is null ? null : Text(work["skill"]),
				Args = Template(work["args"], $"{path}.do.args", check),
				Outputs = work["outputs"] is JsonObject outputs
					? [.. outputs.Select(output => new WorkflowOutputSpec {
						Name = output.Key,
						Type = WorkflowValues.ParseType(Text(output.Value)),
					})]
					: [],
			},
			Gate = stage["gate"] is not null,
			Next = stage["next"] switch {
				null => new WorkflowNext.Sequential(),
				JsonObject choose => new WorkflowNext.Choose(
					Template(choose["prompt"], $"{path}.next.prompt", check)!,
					[.. choose["choices"]!.AsObject().Select(choice => new WorkflowChoice(choice.Key, Text(choice.Value)))],
					choose["max"] is null ? null : int.Parse(Text(choose["max"]), System.Globalization.CultureInfo.InvariantCulture)),
				var target => new WorkflowNext.Goto(Text(target)),
			},
		};
	}

	private static WorkflowTemplate? Template(JsonNode? node, string path, WorkflowCheck check) {
		if (node is null) {
			return null;
		}

		if (!WorkflowTemplate.TryParse(Text(node), out var template, out string error)) {
			check.Add(path, error);
			return null;
		}

		return template;
	}

	private static string Text(JsonNode? node) => node!.GetValue<string>();

	private static WorkflowReadResult Failed(IReadOnlyList<WorkflowDiagnostic> diagnostics) =>
		new() { Definition = null, Diagnostics = diagnostics };
}
