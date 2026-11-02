namespace Weavie.Core.Workflows;

/// <summary>Checks a well-shaped workflow's meaning across fields: ids, targets, and references.</summary>
internal static class WorkflowSemantics {
	public static void Check(WorkflowDefinition definition, string fileId, WorkflowCheck check) {
		if (definition.Id != fileId) {
			check.Add("id", $"id \"{definition.Id}\" must match the file name \"{fileId}\"");
		}

		var stageIds = new HashSet<string>(StringComparer.Ordinal);
		for (int i = 0; i < definition.Stages.Count; i++) {
			if (!stageIds.Add(definition.Stages[i].Id)) {
				check.Add($"stages[{i}].id", $"stage id \"{definition.Stages[i].Id}\" is used twice");
			}
		}

		for (int i = 0; i < definition.Stages.Count; i++) {
			CheckStage(definition, definition.Stages[i], $"stages[{i}]", stageIds, check);
		}
	}

	private static void CheckStage(
		WorkflowDefinition definition, WorkflowStage stage, string path, HashSet<string> stageIds, WorkflowCheck check) {
		var work = stage.Do;
		if ((work.Prompt is null) == (work.Skill is null)) {
			check.Add($"{path}.do", "do needs exactly one of prompt or skill");
		}

		if (work.Args is not null && work.Skill is null) {
			check.Add($"{path}.do.args", "args applies only to a skill");
		}

		if (stage.Else is not null) {
			if (stage.When is null) {
				check.Add($"{path}.else", "else applies only with when");
			}

			CheckTarget(stage.Else, $"{path}.else", stageIds, check);
		}

		switch (stage.Next) {
			case WorkflowNext.Goto go:
				CheckTarget(go.Target, $"{path}.next", stageIds, check);
				break;
			case WorkflowNext.Choose choose:
				if (choose.Choices.Count < 2) {
					check.Add($"{path}.next.choices", "a decision needs at least two choices");
				}

				foreach (var choice in choose.Choices) {
					if (choice.Name == WorkflowDecision.Unsure) {
						check.Add($"{path}.next.choices.{choice.Name}", "\"unsure\" is reserved: an unsure answer always pauses the run");
					}

					CheckTarget(choice.Target, $"{path}.next.choices.{choice.Name}", stageIds, check);
				}

				CheckReferences(definition, choose.Prompt, $"{path}.next.prompt", check);
				break;
		}

		CheckReferences(definition, stage.When, $"{path}.when", check);
		CheckReferences(definition, work.Prompt, $"{path}.do.prompt", check);
		CheckReferences(definition, work.Args, $"{path}.do.args", check);
	}

	private static void CheckTarget(string target, string path, HashSet<string> stageIds, WorkflowCheck check) {
		if (target != WorkflowSchema.Done && !stageIds.Contains(target)) {
			check.Add(path, $"\"{target}\" is neither a stage id nor done");
		}
	}

	private static void CheckReferences(WorkflowDefinition definition, WorkflowTemplate? template, string path, WorkflowCheck check) {
		foreach (var reference in template?.References ?? []) {
			bool declared = reference.IsInput
				? definition.Inputs.Any(input => input.Name == reference.Name)
				: definition.Stages.Any(stage => stage.Id == reference.Scope && stage.Do.Outputs.Any(output => output.Name == reference.Name));
			if (!declared) {
				check.Add(path, reference.IsInput
					? $"{reference} names no declared input"
					: $"{reference} names no output declared by a stage \"{reference.Scope}\"");
			}
		}
	}
}
