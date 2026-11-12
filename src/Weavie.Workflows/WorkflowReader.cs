using System.Text.Json.Nodes;

namespace Weavie.Workflows;

/// <summary>A workflow file's definition, or the reasons it has none.</summary>
internal sealed record WorkflowReadResult {
	/// <summary>The definition when the file is valid; otherwise <see langword="null"/>.</summary>
	public required WorkflowDefinition? Definition { get; init; }

	/// <summary>Errors and advisory warnings; warnings may accompany a valid definition.</summary>
	public required IReadOnlyList<WorkflowDiagnostic> Diagnostics { get; init; }
}

/// <summary>Reads workflow file text: YAML, then the schema's shape, then the meaning across fields.</summary>
internal static class WorkflowReader {
	/// <summary>Reads <paramref name="text"/> from a file named <paramref name="fileId"/> (<c>quick-fix.yaml</c> → <c>quick-fix</c>).</summary>
	public static WorkflowReadResult Read(string text, string fileId) => Read(text, _ => fileId);

	/// <summary>Reads proposed <paramref name="text"/> that has no file yet, so its own id stands in for the file name.</summary>
	public static WorkflowReadResult ReadProposal(string text) => Read(text, definition => definition.Id);

	private static WorkflowReadResult Read(string text, Func<WorkflowDefinition, string> fileId) {
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
			Agents = root["agents"] is JsonObject agents
				? [.. agents.Select(agent => new WorkflowAgent {
					Name = agent.Key,
					Provider = Text(agent.Value!["provider"]),
					Model = agent.Value!["model"] is { } model ? Text(model) : null,
					Options = agent.Value!["options"] is JsonObject options
						? options.ToDictionary(option => option.Key, option => Text(option.Value), StringComparer.Ordinal)
						: new Dictionary<string, string>(),
				})]
				: [],
			Stages = Resolve([.. root["stages"]!.AsArray().Select((stage, i) => ReadStage(stage!.AsObject(), $"stages[{i}]", check))]),
			SourceText = text,
		};
		WorkflowSemantics.Check(definition, fileId(definition), check);
		return check.HasErrors ? Failed(check.Diagnostics) : new WorkflowReadResult { Definition = definition, Diagnostics = check.Diagnostics };
	}

	// A reusing stage opens its conversation as the stage it names does; WorkflowSemantics reports a name that can't.
	private static List<WorkflowStage> Resolve(List<WorkflowStage> stages) => [.. stages.Select(stage =>
		stage.Run.Reuse is { } reused && stages.FirstOrDefault(s => s.Id == reused) is { Run.Reuse: null, Run.Context: not WorkflowContext.Continue } opener
			? stage with { Run = stage.Run with { Context = opener.Run.Context } }
			: stage)];

	private static WorkflowStage ReadStage(JsonObject stage, string path, WorkflowCheck check) {
		var work = stage["run"]!.AsObject();
		return new WorkflowStage {
			Id = Text(stage["id"]),
			If = stage["if"] is { } condition ? Question(condition, $"{path}.if", check) : null,
			Run = new WorkflowWork {
				Context = work["context"] switch {
					null => WorkflowContext.Continue,
					JsonObject => WorkflowContext.Fresh,
					var context => Text(context) switch {
						"continue" => WorkflowContext.Continue,
						"fork" => WorkflowContext.Fork,
						"fresh" => WorkflowContext.Fresh,
						_ => throw new InvalidOperationException("The workflow schema accepted an unknown conversation context."),
					},
				},
				Reuse = work["context"] is JsonObject reuse ? Text(reuse["reuse"]) : null,
				Prompt = Template(work["prompt"], $"{path}.run.prompt", check),
				Skill = work["skill"] is null ? null : Text(work["skill"]),
				Args = Template(work["args"], $"{path}.run.args", check),
				Outputs = work["outputs"] is JsonObject outputs
					? [.. outputs.Select(output => new WorkflowOutputSpec {
						Name = output.Key,
						Type = WorkflowValues.ParseType(Text(output.Value)),
					})]
					: [],
				Agent = work["agent"] is { } agent ? Text(agent) : null,
				Role = work["role"] is { } role ? Text(role) : null,
			},
			Then = stage["then"] switch {
				null => new WorkflowThen.Sequential(),
				JsonObject decide => new WorkflowThen.Decide(
					Question(decide, $"{path}.then", check),
					[.. decide["choices"]!.AsObject().Select(choice => choice.Value is JsonObject described
						? new WorkflowChoice(choice.Key, Text(described["then"]), described["description"] is { } description ? Text(description) : null)
						: new WorkflowChoice(choice.Key, Text(choice.Value), null))],
					decide["limit"] is null ? null : int.Parse(Text(decide["limit"]), System.Globalization.CultureInfo.InvariantCulture)),
				var target => new WorkflowThen.Goto(Text(target)),
			},
		};
	}

	// A question is text (the model answers) or a mapping with `ask` and an optional `by`.
	private static WorkflowQuestion Question(JsonNode node, string path, WorkflowCheck check) => node is JsonObject mapping
		? new WorkflowQuestion {
			Ask = Template(mapping["ask"], $"{path}.ask", check) ?? WorkflowTemplate.Empty,
			Confidence = mapping["confidence"] is { } confidence ? double.Parse(Text(confidence), System.Globalization.CultureInfo.InvariantCulture) : null,
			By = mapping["by"] is { } by && Text(by) == "user" ? WorkflowAnswerer.User : WorkflowAnswerer.Model,
		}
		: new WorkflowQuestion { Ask = Template(node, path, check) ?? WorkflowTemplate.Empty, By = WorkflowAnswerer.Model, Confidence = null };

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
