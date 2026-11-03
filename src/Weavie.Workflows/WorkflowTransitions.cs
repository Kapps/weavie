using System.Text.Json;

namespace Weavie.Workflows;

/// <summary>
/// The pure state machine of a run: each transition takes a run and returns the next run plus the effects its
/// owner must perform. Nothing here touches files, threads, or the agent.
/// </summary>
public sealed partial class WorkflowTransitions(WorkflowDefinition definition, int maxStageAttempts, string worktree) {
	private const string Yes = "yes";
	private const string No = "no";

	/// <summary>A new run of the definition with <paramref name="inputs"/>, entering its first stage.</summary>
	public WorkflowStep Start(string runId, string sourcePath, Dictionary<string, WorkflowValue> inputs) {
		var run = new WorkflowRun {
			Id = runId,
			WorkflowId = definition.Id,
			SourcePath = sourcePath,
			SourceText = definition.SourceText,
			Inputs = inputs,
			Stage = definition.Stages[0].Id,
			Phase = WorkflowPhase.Open,
			Decision = null,
			Asked = null,
			Attempts = 0,
			Outputs = [],
			Summary = "",
			Streaks = [],
			Note = "",
			PausedReason = null,
			Kickoff = null,
		};
		return Enter(run, definition.Stages[0].Id);
	}

	/// <summary>The agent reports the current stage done with <paramref name="outputs"/> and a <paramref name="summary"/>.</summary>
	public WorkflowCompletion Complete(WorkflowRun run, JsonElement outputs, string summary) {
		Require(run, WorkflowPhase.Open, "complete");
		var stage = StageOf(run.Stage);
		var errors = new List<string>();
		var values = ReadOutputs(stage, outputs, errors);
		if (errors.Count > 0) {
			int attempts = run.Attempts + 1;
			return new WorkflowCompletion(attempts >= maxStageAttempts
				? Pause(run with { Attempts = attempts }, $"Stage \"{stage.Id}\" was rejected {attempts} times: {string.Join("; ", errors)}")
				: new WorkflowStep(run with { Attempts = attempts }, []), errors);
		}

		run = run with {
			Outputs = new(run.Outputs) { [stage.Id] = values },
			Summary = summary,
			Attempts = 0,
			Kickoff = null,
		};
		return new WorkflowCompletion(Advance(run, stage), []);
	}

	/// <summary>The agent can't finish the current stage.</summary>
	public WorkflowStep Fail(WorkflowRun run, string reason) {
		Require(run, WorkflowPhase.Open, "fail");
		return Pause(run with { Kickoff = null }, $"The agent couldn't finish stage \"{run.Stage}\": {reason}");
	}

	/// <summary>The user answers the question the run is asking, with <paramref name="feedback"/> (empty for none).</summary>
	public WorkflowStep Answer(WorkflowRun run, string choice, string feedback) {
		Require(run, WorkflowPhase.Asking, "answer");
		if (!run.Asked!.Choices.Contains(choice)) {
			throw new WorkflowException($"\"{choice}\" isn't an answer to this question; answer one of {string.Join(", ", run.Asked.Choices)}.");
		}

		return Route(run with { Streaks = [] }, choice, $"The user answered \"{choice}\"", feedback);
	}

	/// <summary>The model's answer to the decision in flight (or why there is none).</summary>
	public WorkflowStep Resolve(WorkflowRun run, WorkflowDecisionResult result) {
		Require(run, WorkflowPhase.Deciding, "resolve a decision for");
		var choices = run.Asked?.Choices ?? [];
		if (result.Choice is not { } choice) {
			return AskUser(run, $"the decision model failed ({result.Failure})");
		}

		if (choice == WorkflowDecision.Unsure) {
			return AskUser(run, "the decision model was unsure");
		}

		if (!choices.Contains(choice)) {
			return AskUser(run, $"the decision model answered \"{choice}\", which isn't one of the choices");
		}

		if (run.Decision == WorkflowDecisionKind.Then) {
			var decide = (WorkflowThen.Decide)StageOf(run.Stage).Then;
			int count = run.Streaks.GetValueOrDefault(run.Stage) + 1;
			if (decide.Limit is { } limit && count > limit) {
				return AskUser(run, $"the decision model has answered this {limit} times in a row, the limit");
			}

			run = run with { Streaks = new() { [run.Stage] = count } };
		}

		return Route(run, choice, $"Weavie decided \"{choice}\"", "");
	}

	/// <summary>The user resumes a paused run: a paused decision is asked again, otherwise the stage restarts.</summary>
	public WorkflowStep Resume(WorkflowRun run) {
		Require(run, WorkflowPhase.Paused, "resume");
		var stage = StageOf(run.Stage);
		run = run with { PausedReason = null, Attempts = 0 };
		return run.Decision switch {
			WorkflowDecisionKind.If => Enter(run, stage.Id),
			WorkflowDecisionKind.Then => Advance(run, stage),
			_ => Open(run, stage),
		};
	}

	/// <summary>The user moves the run to <paramref name="stageId"/>, which opens regardless of its <c>if</c>.</summary>
	public WorkflowStep Jump(WorkflowRun run, string stageId) {
		if (run.IsFinished) {
			throw new WorkflowException($"Run {run.Id} has ended.");
		}

		return Open(run with { PausedReason = null }, StageOf(stageId));
	}

	/// <summary>The user cancels the run.</summary>
	public WorkflowStep Cancel(WorkflowRun run) {
		if (run.IsFinished) {
			throw new WorkflowException($"Run {run.Id} has ended.");
		}

		return new WorkflowStep(run with { Phase = WorkflowPhase.Cancelled, Decision = null, Asked = null, Note = "", Kickoff = null }, []);
	}

	// Where an answer leads. Back to the current stage reopens it with a short turn; anywhere else enters that stage.
	private WorkflowStep Route(WorkflowRun run, string choice, string answered, string feedback) {
		var stage = StageOf(run.Stage);
		if (feedback.Length > 0) {
			run = run with { Note = Join(run.Note, $"{answered}: {feedback}") };
		}

		if (run.Decision == WorkflowDecisionKind.If) {
			run = run with { Decision = null, Asked = null };
			return choice == Yes ? Open(run, stage) : Enter(run, Following(stage));
		}

		string target = ((WorkflowThen.Decide)stage.Then).Choices.First(c => c.Name == choice).Target;
		if (target != stage.Id) {
			return Enter(run, target);
		}

		var kickoff = WorkflowKickoff.Prompt(
			$"{answered} to \"{run.Asked?.Question ?? ""}\", so stage \"{stage.Id}\" continues."
			+ (run.Note.Length == 0 ? "" : $" Feedback: {run.Note}")
			+ " Revise the work accordingly, then call completeStage again.");
		return new WorkflowStep(
			run with { Phase = WorkflowPhase.Open, Decision = null, Asked = null, Attempts = 0, Note = "", Kickoff = kickoff },
			[new WorkflowEffect.Kickoff(kickoff)]);
	}

	private WorkflowStep Enter(WorkflowRun run, string target) {
		run = run with { Decision = null, Asked = null, PausedReason = null, Attempts = 0, Kickoff = null };
		if (target == WorkflowSchema.Done) {
			return new WorkflowStep(
				run with { Phase = WorkflowPhase.Done, Note = "" },
				[new WorkflowEffect.Notify($"Workflow {definition.Id} finished."
					+ (run.Note.Length == 0 ? "" : $" Feedback with no stage left to receive it: {run.Note}"))]);
		}

		var stage = StageOf(target);
		run = run with { Stage = stage.Id };
		return stage.If is { } condition
			? Ask(run, WorkflowDecisionKind.If, condition, [Yes, No], new Dictionary<string, string>())
			: Open(run, stage);
	}

	private WorkflowStep Advance(WorkflowRun run, WorkflowStage stage) => stage.Then switch {
		WorkflowThen.Goto go => Enter(run, go.Target),
		WorkflowThen.Decide decide => Ask(
			run, WorkflowDecisionKind.Then, decide.Question, [.. decide.Choices.Select(c => c.Name)],
			run.Outputs.GetValueOrDefault(stage.Id, []).ToDictionary(o => o.Key, o => o.Value.Render())),
		_ => Enter(run, Following(stage)),
	};

	// Opens the stage with its rendered work, prefixed by any feedback the user's last answer left for it.
	private WorkflowStep Open(WorkflowRun run, WorkflowStage stage) {
		if (!TryRender(run, stage.Run.Prompt ?? stage.Run.Args, out string rendered, out string missing)) {
			return Pause(run with { Stage = stage.Id }, missing);
		}

		var kickoff = stage.Run.Skill is { } skill
			? new WorkflowKickoff { Text = $"/{skill} {Join(rendered, run.Note)}".TrimEnd(), Command = skill }
			: WorkflowKickoff.Prompt(Join(run.Note, rendered));
		return new WorkflowStep(
			run with { Stage = stage.Id, Phase = WorkflowPhase.Open, Decision = null, Asked = null, Attempts = 0, Note = "", Kickoff = kickoff },
			[new WorkflowEffect.Kickoff(kickoff)]);
	}

	private WorkflowStep Ask(
		WorkflowRun run, WorkflowDecisionKind kind, WorkflowQuestion question, IReadOnlyList<string> choices,
		IReadOnlyDictionary<string, string> outputs) {
		if (!TryRender(run, question.Ask, out string rendered, out string missing)) {
			return Pause(run with { Decision = kind }, missing);
		}

		run = run with { Decision = kind };
		if (question.By == WorkflowAnswerer.User) {
			return AskUser(run with { Asked = new WorkflowAsked { Question = rendered, Choices = choices, Reason = "" } }, "");
		}

		return new WorkflowStep(
			run with { Phase = WorkflowPhase.Deciding, Asked = new WorkflowAsked { Question = rendered, Choices = choices, Reason = "" } },
			[new WorkflowEffect.Decide(new WorkflowDecisionRequest {
				Question = rendered, Choices = choices, Outputs = outputs,
				Summary = run.Note.Length == 0 ? run.Summary : Join(run.Summary, $"User feedback: {run.Note}"),
			})]);
	}

	// Puts the run's current question to the user; a non-empty reason says why the model didn't answer it.
	private static WorkflowStep AskUser(WorkflowRun run, string reason) {
		var asked = run.Asked! with { Reason = reason };
		return new WorkflowStep(
			run with { Phase = WorkflowPhase.Asking, Asked = asked, Kickoff = null },
			[new WorkflowEffect.Notify($"Workflow {run.WorkflowId} needs your answer at stage \"{run.Stage}\": {asked.Question}"
				+ (reason.Length == 0 ? "" : $" ({reason})"))]);
	}

	private static WorkflowStep Pause(WorkflowRun run, string reason) => new(
		run with { Phase = WorkflowPhase.Paused, PausedReason = reason, Kickoff = null },
		[new WorkflowEffect.Notify($"Workflow {run.WorkflowId} paused: {reason}")]);

	private static string Join(string first, string second) =>
		first.Length == 0 ? second : second.Length == 0 ? first : $"{first}\n\n{second}";

	private static void Require(WorkflowRun run, WorkflowPhase phase, string action) {
		if (run.Phase != phase) {
			throw new WorkflowException($"Can't {action} stage \"{run.Stage}\" of run {run.Id}: it is {Describe(run.Phase)}.");
		}
	}

	private static string Describe(WorkflowPhase phase) => phase switch {
		WorkflowPhase.Open => "open",
		WorkflowPhase.Deciding => "deciding what comes next",
		WorkflowPhase.Asking => "waiting for the user's answer",
		WorkflowPhase.Paused => "paused",
		WorkflowPhase.Done => "done",
		_ => "cancelled",
	};
}
