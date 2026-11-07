using System.Text.Json.Nodes;

namespace Weavie.Workflows;

/// <summary>A workflow file's definition, or the reasons it has none.</summary>
internal sealed record WorkflowReadResult {
	/// <summary>The definition when the file is valid; otherwise <see langword="null"/>.</summary>
	public required WorkflowDefinition? Definition { get; init; }

	/// <summary>Every problem found; empty exactly when <see cref="Definition"/> is set.</summary>
	public required IReadOnlyList<WorkflowDiagnostic> Diagnostics { get; init; }
}

/// <summary>Reads workflow file text: YAML, then the schema's shape, then the meaning across fields.</summary>
internal static class WorkflowReader {
	/// <summary>Reads <paramref name="text"/> from a file named <paramref name="fileId"/> (<c>quick-fix.yaml</c> → <c>quick-fix</c>).</summary>
	public static WorkflowReadResult Read(string text, string fileId) {
		YamlDocument document;
		try {
			document = YamlNodeReader.Read(text);
		} catch (WorkflowFormatException ex) {
			return Failed([WorkflowDiagnostic.Error("", ex.Position, ex.Message)]);
		}

		var check = new WorkflowCheck(document.Positions);
		WorkflowSchema.Root.Check(document.Root, "", check);
		if (check.HasErrors) {
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
		return check.HasErrors ? Failed(check.Diagnostics) : new WorkflowReadResult { Definition = definition, Diagnostics = check.Diagnostics };
	}

	private static WorkflowStage ReadStage(JsonObject stage, string path, WorkflowCheck check) {
		var work = stage["run"]!.AsObject();
		return new WorkflowStage {
			Id = Text(stage["id"]),
			If = stage["if"] is { } condition ? Question(condition, $"{path}.if", check) : null,
			Run = new WorkflowWork {
				Prompt = Template(work["prompt"], $"{path}.run.prompt", check),
				Skill = work["skill"] is null ? null : Text(work["skill"]),
				Args = Template(work["args"], $"{path}.run.args", check),
				Outputs = work["outputs"] is JsonObject outputs
					? [.. outputs.Select(output => new WorkflowOutputSpec {
						Name = output.Key,
						Type = WorkflowValues.ParseType(Text(output.Value)),
					})]
					: [],
			},
			Then = stage["then"] switch {
				null => new WorkflowThen.Sequential(),
				JsonObject decide => new WorkflowThen.Decide(
					Question(decide, $"{path}.then", check),
					[.. decide["choices"]!.AsObject().Select(choice => new WorkflowChoice(choice.Key, Text(choice.Value)))],
					decide["limit"] is null ? null : int.Parse(Text(decide["limit"]), System.Globalization.CultureInfo.InvariantCulture)),
				var target => new WorkflowThen.Goto(Text(target)),
			},
		};
	}

	// A question is text (the model answers) or a mapping with `ask` and an optional `by`.
	private static WorkflowQuestion Question(JsonNode node, string path, WorkflowCheck check) => node is JsonObject mapping
		? new WorkflowQuestion {
			Ask = Template(mapping["ask"], $"{path}.ask", check) ?? WorkflowTemplate.Empty,
			By = mapping["by"] is { } by && Text(by) == "user" ? WorkflowAnswerer.User : WorkflowAnswerer.Model,
		}
		: new WorkflowQuestion { Ask = Template(node, path, check) ?? WorkflowTemplate.Empty, By = WorkflowAnswerer.Model };

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
