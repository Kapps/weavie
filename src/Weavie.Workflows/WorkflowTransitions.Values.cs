using System.Text.Json;

namespace Weavie.Workflows;

internal sealed partial class WorkflowTransitions {
	/// <summary>Reads a run's inputs for <paramref name="definition"/>; throws listing every problem.</summary>
	public static Dictionary<string, WorkflowValue> ReadInputs(WorkflowDefinition definition, JsonElement inputs, string worktree, string? documentsDirectory) {
		var errors = new List<string>();
		if (inputs.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null)) {
			throw new WorkflowException("Workflow inputs must be an object of name → value.");
		}

		var values = new Dictionary<string, WorkflowValue>(StringComparer.Ordinal);
		foreach (var spec in definition.Inputs) {
			if (inputs.ValueKind != JsonValueKind.Object || !inputs.TryGetProperty(spec.Name, out var element)) {
				if (!spec.Optional) errors.Add($"missing input \"{spec.Name}\"");
				continue;
			}

			if (WorkflowValues.TryRead(element, spec.Type, worktree, documentsDirectory, out var value, out string error)) {
				values[spec.Name] = value;
			} else {
				errors.Add($"input \"{spec.Name}\": {error}");
			}
		}

		if (inputs.ValueKind == JsonValueKind.Object) {
			errors.AddRange(inputs.EnumerateObject()
				.Where(p => definition.Inputs.All(spec => spec.Name != p.Name))
				.Select(p => $"unknown input \"{p.Name}\""));
		}

		return errors.Count == 0
			? values
			: throw new WorkflowException($"Workflow {definition.Id} can't start: {string.Join("; ", errors)}");
	}

	private Dictionary<string, WorkflowValue> ReadOutputs(WorkflowStage stage, JsonElement outputs, string documentsDirectory, List<string> errors) {
		var values = new Dictionary<string, WorkflowValue>(StringComparer.Ordinal);
		bool isObject = outputs.ValueKind == JsonValueKind.Object;
		if (!isObject && outputs.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)) {
			errors.Add("outputs must be an object of name → value");
			return values;
		}

		foreach (var spec in stage.Run.Outputs) {
			if (!isObject || !outputs.TryGetProperty(spec.Name, out var element)) {
				errors.Add($"missing output \"{spec.Name}\" ({spec.Type.ToString().ToLowerInvariant()})");
			} else if (WorkflowValues.TryRead(element, spec.Type, worktree, documentsDirectory, out var value, out string error)) {
				values[spec.Name] = value;
			} else {
				errors.Add($"output \"{spec.Name}\": {error}");
			}
		}

		if (isObject) {
			errors.AddRange(outputs.EnumerateObject()
				.Where(p => stage.Run.Outputs.All(spec => spec.Name != p.Name))
				.Select(p => $"stage \"{stage.Id}\" declares no output \"{p.Name}\""));
		}

		return values;
	}

	private bool TryRender(WorkflowRun run, WorkflowTemplate? template, out string text, out string missing) {
		text = "";
		missing = "";
		if (template is null) {
			return true;
		}

		var absent = template.References.FirstOrDefault(r => !r.IsInput && !r.IsDocuments && run.Outputs.GetValueOrDefault(r.Scope)?.ContainsKey(r.Name) != true);
		if (absent is not null) {
			missing = $"Stage \"{run.Stage}\" uses {absent}, but stage \"{absent.Scope}\" hasn't reported it.";
			return false;
		}

		text = template.Render(r => ResolveValue(run, r));
		return true;
	}

	private static WorkflowValue ResolveValue(WorkflowRun run, WorkflowReference reference) => reference.IsDocuments
		? new WorkflowValue { Text = run.DocumentsDirectory } : reference.IsInput
		? run.Inputs.GetValueOrDefault(reference.Name, WorkflowValue.Empty)
		: run.Outputs[reference.Scope][reference.Name];

	private bool TrySelectFiles(WorkflowRun run, WorkflowTemplate template, out IReadOnlyList<string> paths, out string error) {
		var selected = new List<string>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		paths = selected;
		foreach (var reference in template.References.Where(r => WorkflowReferences.TypeOf(definition, r) == WorkflowValueType.Files)) {
			foreach (string path in ResolveValue(run, reference).Items ?? []) {
				if (!WorkflowFilePaths.TryResolve(worktree, run.DocumentsDirectory, path, out string relative, out string full)) {
					error = $"\"{path}\" isn't a file path inside the worktree or run documents directory";
					return false;
				}
				if (seen.Add(full)) selected.Add(relative);
			}
		}
		error = "";
		return true;
	}

	private WorkflowStage StageOf(string id) =>
		definition.Stages.FirstOrDefault(s => s.Id == id)
		?? throw new WorkflowException($"Workflow {definition.Id} has no stage \"{id}\".");

	private string Following(WorkflowStage stage) {
		int index = definition.Stages.ToList().IndexOf(stage);
		return index + 1 < definition.Stages.Count ? definition.Stages[index + 1].Id : WorkflowSchema.Done;
	}
}
