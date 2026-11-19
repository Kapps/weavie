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
			Stages = [.. root["stages"]!.AsArray().Select((stage, i) => ReadStage(stage!.AsObject(), $"stages[{i}]", check))],
			SourceText = text,
		};
		definition = definition with { Stages = Resolve(definition.Stages) };
		WorkflowSemantics.Check(definition, fileId(definition), check);
		return check.HasErrors ? Failed(check.Diagnostics) : new WorkflowReadResult { Definition = definition, Diagnostics = check.Diagnostics };
	}

	// A reusing stage opens its conversation as the sibling it names does, and a copied output takes its source's type;
	// WorkflowSemantics reports a name that can't.
	private static List<WorkflowStage> Resolve(IReadOnlyList<WorkflowStage> stages) => [.. stages.Select(stage => {
		var children = Resolve(stage.Stages);
		return stage with {
			Stages = children,
			Context = stage.Reuse is { } reused && stages.FirstOrDefault(s => s.Id == reused) is { Reuse: null, Context: not WorkflowContext.Continue } opener
				? opener.Context : stage.Context,
			Outputs = [.. stage.Outputs.Select(output => output.Source is { } source
				&& (stage with { Stages = children }).Copied(source) is { } copied ? output with { Type = copied.Type } : output)],
		};
	})];

	private static WorkflowStage ReadStage(JsonObject stage, string path, WorkflowCheck check) {
		var stages = stage["stages"] is JsonArray list
			? [.. list.Select((child, i) => ReadStage(child!.AsObject(), $"{path}.stages[{i}]", check))]
			: new List<WorkflowStage>();
		return new WorkflowStage {
			Id = Text(stage["id"]),
			If = stage["if"] is { } condition ? Question(condition, $"{path}.if", check) : null,
			Foreach = Template(stage["foreach"], $"{path}.foreach", check),
			Context = stage["context"] switch {
				null => WorkflowContext.Continue,
				JsonObject => WorkflowContext.Fresh,
				var context => Text(context) switch {
					"continue" => WorkflowContext.Continue,
					"fork" => WorkflowContext.Fork,
					"fresh" => WorkflowContext.Fresh,
					_ => throw new InvalidOperationException("The workflow schema accepted an unknown conversation context."),
				},
			},
			Reuse = stage["context"] is JsonObject reuse ? Text(reuse["reuse"]) : null,
			Agent = stage["agent"] is { } agent ? Text(agent) : null,
			Role = stage["role"] is { } role ? Text(role) : null,
			Run = stage["run"] is JsonObject work ? new WorkflowWork {
				Prompt = Template(work["prompt"], $"{path}.run.prompt", check),
				Skill = work["skill"] is null ? null : Text(work["skill"]),
				Args = Template(work["args"], $"{path}.run.args", check),
			} : null,
			Stages = stages,
			Outputs = stage["outputs"] is JsonObject outputs
				? [.. outputs.Select(output => Output(output.Key, Text(output.Value), stages.Count > 0, $"{path}.outputs.{output.Key}", check))]
				: [],
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

	// A stage with work declares a type; a stage with stages names the output of one of them it copies.
	private static WorkflowOutputSpec Output(string name, string value, bool holdsStages, string path, WorkflowCheck check) {
		if (!holdsStages) {
			if (WorkflowSchema.ValueTypes.Contains(value)) return new() { Name = name, Type = WorkflowValues.ParseType(value), Source = null };
			check.Add(path, $"\"{value}\" is not a type; a stage with run declares text, url, list, files, or pr");
		} else if (WorkflowTemplate.TryParse(value, out var template, out _) && template.IsSingleReference(out var source)) {
			return new() { Name = name, Type = WorkflowValueType.Text, Source = source };
		} else {
			check.Add(path, "a stage with stages takes each output from one of its stages: \"{{stage.output}}\"");
		}
		return new() { Name = name, Type = WorkflowValueType.Text, Source = null };
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
