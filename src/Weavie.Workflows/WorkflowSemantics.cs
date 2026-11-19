namespace Weavie.Workflows;

/// <summary>Checks a well-shaped workflow's meaning across fields: ids, targets, conversations, and which values each field reads.</summary>
internal static class WorkflowSemantics {
	private enum Field { If, Foreach, Run, Then }

	public static void Check(WorkflowDefinition definition, string fileId, WorkflowCheck check) {
		if (definition.Id != fileId) {
			check.Add("id", $"id \"{definition.Id}\" must match the file name \"{fileId}\"");
		}

		// The tree's lookups key on ids, so its other checks only run once every id is unique.
		var seen = new HashSet<string>(StringComparer.Ordinal);
		bool unique = true;
		foreach (var (stage, path) in Paths(definition.Stages, "stages")) {
			if (stage.Id == "run") {
				check.Add($"{path}.id", "stage id \"run\" is reserved for run references");
			}
			if (!seen.Add(stage.Id)) {
				check.Add($"{path}.id", $"stage id \"{stage.Id}\" is used twice");
				unique = false;
			}
		}

		if (!unique) return;
		foreach (var (stage, path) in Paths(definition.Stages, "stages")) {
			CheckStage(definition, stage, path, check);
		}
	}

	private static IEnumerable<(WorkflowStage Stage, string Path)> Paths(IReadOnlyList<WorkflowStage> list, string path) =>
		list.SelectMany((stage, i) => new[] { (stage, $"{path}[{i}]") }.Concat(Paths(stage.Stages, $"{path}[{i}].stages")));

	private static void CheckStage(WorkflowDefinition definition, WorkflowStage stage, string path, WorkflowCheck check) {
		if ((stage.Run is null) == (stage.Stages.Count == 0)) {
			check.Add(path, "a stage has exactly one of run or stages");
		}

		if (stage.Run is { } work) {
			if ((work.Prompt is null) == (work.Skill is null)) {
				check.Add($"{path}.run", "run needs exactly one of prompt or skill");
			}
			if (work.Args is not null && work.Skill is null) {
				check.Add($"{path}.run.args", "args applies only to a skill");
			}
		}

		CheckAgent(definition, stage, path, check);
		if (stage.Reuse is { } reused) {
			CheckReuse(definition, stage, reused, path, check);
		}

		if (stage.Repeats) {
			foreach (var output in stage.Outputs.Where(o => WorkflowReferences.EntryNames.Contains(o.Name))) {
				check.Add($"{path}.outputs.{output.Name}", $"\"{output.Name}\" names the current entry of a repeated stage");
			}
			CheckForeach(definition, stage, path, check);
		}

		foreach (var output in stage.Outputs.Where(o => o.Source is { } source && stage.Copied(source) is null)) {
			check.Add($"{path}.outputs.{output.Name}", $"{output.Source} names no output of a stage inside \"{stage.Id}\"");
		}

		if (stage.If is { } condition) {
			CheckQuestion(definition, stage, condition, Field.If, $"{path}.if", check);
		}

		var siblings = definition.List(stage);
		switch (stage.Then) {
			case WorkflowThen.Goto go:
				CheckTarget(stage, go.Target, siblings, $"{path}.then", check);
				break;
			case WorkflowThen.Decide decide:
				if (decide.Choices.Count < 2) {
					check.Add($"{path}.then.choices", "a decision needs at least two choices");
				}

				foreach (var choice in decide.Choices) {
					CheckTarget(stage, choice.Target, siblings, $"{path}.then.choices.{choice.Name}", check);
				}

				if (decide.Limit is not null && decide.Question.By == WorkflowAnswerer.User) {
					check.Add($"{path}.then.limit", "limit applies only to decisions the model answers");
				}

				CheckQuestion(definition, stage, decide.Question, Field.Then, $"{path}.then", check);
				WorkflowEvidenceWarnings.CheckOutputs(definition, stage, decide.Question, $"{path}.then.ask", check);
				break;
		}

		CheckReferences(definition, stage, stage.Run?.Prompt, Field.Run, $"{path}.run.prompt", check);
		CheckReferences(definition, stage, stage.Run?.Args, Field.Run, $"{path}.run.args", check);
	}

	// A conversation never switches model: only a stage that opens one, or one running in main, names an agent.
	private static void CheckAgent(WorkflowDefinition definition, WorkflowStage stage, string path, WorkflowCheck check) {
		if (stage.Agent is not null && stage.Role is not null) {
			check.Add($"{path}.role", "a stage takes agent or role, not both");
		}

		if (stage.Agent is { } agent && definition.Agents.All(a => a.Name != agent)) {
			check.Add($"{path}.agent", definition.Agents.Count == 0
				? $"\"{agent}\" names no agent; this workflow defines no agents"
				: $"\"{agent}\" names no agent; defined: {string.Join(", ", definition.Agents.Select(a => a.Name))}");
		}

		if ((stage.Agent ?? stage.Role) is not { } asks || stage.Reuse is not null || stage.Context != WorkflowContext.Continue) return;
		string field = $"{path}.{(stage.Agent is null ? "role" : "agent")}";
		if (stage.Stages.Count > 0) {
			check.Add(field, $"a stage with stages runs no turn of its own; give it fork or fresh to run its stages on \"{asks}\"");
		} else if (definition.Parent(stage) is { } parent && definition.Opener(parent) is { } opener) {
			check.Add(field, $"stage \"{stage.Id}\" runs in the conversation of \"{opener.Id}\"; give it fork or fresh to run on \"{asks}\"");
		}
	}

	private static void CheckReuse(WorkflowDefinition definition, WorkflowStage stage, string reused, string path, WorkflowCheck check) {
		string? problem = definition.List(stage).FirstOrDefault(s => s.Id == reused) switch {
			null => definition.TryStage(reused) is null ? $"\"{reused}\" names no stage" : $"\"{reused}\" is not a stage beside \"{stage.Id}\"",
			{ Id: var id } when id == stage.Id => "a stage can't reuse its own conversation; fresh already keeps one for the run",
			{ Reuse: { } further } => $"stage \"{reused}\" reuses \"{further}\"; name \"{further}\"",
			{ Context: WorkflowContext.Continue } => $"stage \"{reused}\" runs in the conversation it sits in; reuse names a fork or fresh stage",
			{ Repeats: true } => $"stage \"{reused}\" opens a conversation per entry; reuse names one that is not repeated",
			_ => null,
		};
		if (problem is not null) check.Add($"{path}.context.reuse", problem);
		if (stage.Agent is not null || stage.Role is not null) {
			check.Add($"{path}.{(stage.Agent is null ? "role" : "agent")}",
				$"a stage that reuses a conversation runs on that conversation's agent; set it on \"{reused}\"");
		}
	}

	private static void CheckForeach(WorkflowDefinition definition, WorkflowStage stage, string path, WorkflowCheck check) {
		if (!stage.Foreach!.IsSingleReference(out var reference)) {
			check.Add($"{path}.foreach", "foreach takes one reference to a list");
			return;
		}
		if (!CheckReferences(definition, stage, stage.Foreach, Field.Foreach, $"{path}.foreach", check)) return;
		if (WorkflowReferences.TypeOf(definition, reference) is not (WorkflowValueType.List or WorkflowValueType.Files)
			&& !WorkflowReferences.IsRepeated(definition, reference)) {
			check.Add($"{path}.foreach", $"{reference} is not a list");
		}
	}

	// A then decision also sees the stage's output values.
	private static void CheckQuestion(
		WorkflowDefinition definition, WorkflowStage stage, WorkflowQuestion question, Field field, string path, WorkflowCheck check) {
		bool seesOutputs = field == Field.Then && stage.Outputs.Count > 0;
		if (question.By == WorkflowAnswerer.User && question.Confidence is not null) {
			check.Add($"{path}.confidence", "confidence applies only to decisions the model answers");
		}
		string askPath = check.QuestionPath(path);
		CheckReferences(definition, stage, question.Ask, field, askPath, check);
		WorkflowEvidenceWarnings.CheckQuestion(question, askPath, seesOutputs, check);
	}

	private static void CheckTarget(WorkflowStage stage, string target, IReadOnlyList<WorkflowStage> siblings, string path, WorkflowCheck check) {
		if (target != WorkflowSchema.Done && siblings.All(s => s.Id != target)) {
			check.Add(path, $"\"{target}\" is not a stage beside \"{stage.Id}\"; then reaches the stages in the same list, or done");
		}
	}

	// True when every reference names a value this field may read.
	private static bool CheckReferences(
		WorkflowDefinition definition, WorkflowStage from, WorkflowTemplate? template, Field field, string path, WorkflowCheck check) {
		bool ok = true;
		foreach (var reference in template?.References ?? []) {
			if (Problem(definition, from, reference, field) is { } problem) {
				check.Add(path, problem);
				ok = false;
			}
		}
		return ok;
	}

	private static string? Problem(WorkflowDefinition definition, WorkflowStage from, WorkflowReference reference, Field field) {
		if (reference.IsDocuments || reference.IsPrompt) return null;
		if (reference.IsInput) {
			return WorkflowReferences.TypeOf(definition, reference) is null ? $"{reference} names no declared input" : null;
		}
		if (definition.TryStage(reference.Scope) is not { } target) {
			return $"{reference} names no output declared by a stage \"{reference.Scope}\"";
		}
		if (WorkflowReferences.IsEntry(definition, reference)) {
			return Encloses(definition, target, from) || target == from && field == Field.Run ? null
				: target == from ? $"{reference} is not set here; a repeated stage's if, foreach, and then apply once"
				: $"{reference} is readable only inside repeated stage \"{target.Id}\"";
		}
		if (WorkflowReferences.TypeOf(definition, reference) is null) {
			return $"{reference} names no output declared by a stage \"{reference.Scope}\"";
		}
		if (target == from || Visible(definition, from, target)) return null;
		if (!Encloses(definition, from, target)) return Inside(reference, Hidden(definition, from, target));
		if (definition.Parent(target) != from) return Inside(reference, OutermostBelow(definition, from, target));
		return field != Field.Then ? $"{reference} is set inside stage \"{from.Id}\"; only its outputs and then read it"
			: from.Repeats ? $"{reference} is one entry's value; a repeated stage's then reads its own outputs"
			: null;
	}

	private static string Inside(WorkflowReference reference, WorkflowStage holder) =>
		$"{reference} is inside stage \"{holder.Id}\"; read an output of \"{holder.Id}\" instead";


	// Whether outer holds inner at any depth.
	private static bool Encloses(WorkflowDefinition definition, WorkflowStage outer, WorkflowStage inner) {
		for (var parent = definition.Parent(inner); parent is not null; parent = definition.Parent(parent)) {
			if (parent == outer) return true;
		}
		return false;
	}

	// A stage sees the stages beside it and beside each stage that holds it.
	private static bool Visible(WorkflowDefinition definition, WorkflowStage from, WorkflowStage target) {
		var list = definition.List(target);
		for (var at = from; at is not null; at = definition.Parent(at)) {
			if (definition.List(at) == list) return true;
		}
		return false;
	}

	// The stage holding target that sits in a list from can see.
	private static WorkflowStage Hidden(WorkflowDefinition definition, WorkflowStage from, WorkflowStage target) {
		var at = target;
		while (definition.Parent(at) is { } parent && !Visible(definition, from, parent)) at = parent;
		return definition.Parent(at) ?? at;
	}

	private static WorkflowStage OutermostBelow(WorkflowDefinition definition, WorkflowStage outer, WorkflowStage inner) {
		var at = inner;
		while (definition.Parent(at) is { } parent && parent != outer) at = parent;
		return at;
	}
}
