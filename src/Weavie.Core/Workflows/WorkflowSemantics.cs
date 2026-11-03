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
		var work = stage.Run;
		if ((work.Prompt is null) == (work.Skill is null)) {
			check.Add($"{path}.run", "run needs exactly one of prompt or skill");
		}

		if (work.Args is not null && work.Skill is null) {
			check.Add($"{path}.run.args", "args applies only to a skill");
		}

		if (stage.If is { } condition) {
			CheckQuestion(definition, condition, $"{path}.if", seesOutputs: false, check);
		}

		switch (stage.Then) {
			case WorkflowThen.Goto go:
				CheckTarget(go.Target, $"{path}.then", stageIds, check);
				break;
			case WorkflowThen.Decide decide:
				if (decide.Choices.Count < 2) {
					check.Add($"{path}.then.choices", "a decision needs at least two choices");
				}

				foreach (var choice in decide.Choices) {
					if (choice.Name == WorkflowDecision.Unsure) {
						check.Add($"{path}.then.choices.{choice.Name}", "\"unsure\" is reserved: an unsure model hands the question to the user");
					}

					CheckTarget(choice.Target, $"{path}.then.choices.{choice.Name}", stageIds, check);
				}

				if (decide.Limit is not null && decide.Question.By == WorkflowAnswerer.User) {
					check.Add($"{path}.then.limit", "limit applies only to decisions the model answers");
				}

				CheckQuestion(definition, decide.Question, $"{path}.then.ask", seesOutputs: work.Outputs.Count > 0, check);
				break;
		}

		CheckReferences(definition, work.Prompt, $"{path}.run.prompt", check);
		CheckReferences(definition, work.Args, $"{path}.run.args", check);
	}

	// A model answering with no references and no stage outputs to read can only judge the question's own text.
	private static void CheckQuestion(
		WorkflowDefinition definition, WorkflowQuestion question, string path, bool seesOutputs, WorkflowCheck check) {
		CheckReferences(definition, question.Ask, path, check);
		if (question.By == WorkflowAnswerer.Model && !seesOutputs && !question.Ask.References.Any()) {
			check.Warn(path, "the model answering this has no tools and sees only the question, the stage's outputs, and the "
				+ "agent's summary; reference the values it needs with {{…}}");
		}
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
				: definition.Stages.Any(stage => stage.Id == reference.Scope && stage.Run.Outputs.Any(output => output.Name == reference.Name));
			if (!declared) {
				check.Add(path, reference.IsInput
					? $"{reference} names no declared input"
					: $"{reference} names no output declared by a stage \"{reference.Scope}\"");
			}
		}
	}
}
