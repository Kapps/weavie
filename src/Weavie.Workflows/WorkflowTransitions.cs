using System.Text.Json;

namespace Weavie.Workflows;

/// <summary>
/// The pure state machine of a run: each transition takes a run and returns the next run plus the effects its
/// owner must perform. Nothing here touches files, threads, or the agent.
/// </summary>
internal sealed partial class WorkflowTransitions(
	WorkflowDefinition definition, int maxStageAttempts, bool openReviewItem, double decisionConfidence, string worktree, WorkflowVocabulary words) {
	private const string Yes = "yes";
	private const string No = "no";

	/// <summary>
	/// A new run of the definition with <paramref name="inputs"/> and <paramref name="prompt"/>, entering its first
	/// stage; it keeps the predecessor's <paramref name="hold"/>, whose own values are still owed back to the session.
	/// </summary>
	public WorkflowStep Start(
		string runId, string sourcePath, Dictionary<string, WorkflowValue> inputs, string documentsDirectory, WorkflowHold? hold, WorkflowPrompt prompt) {
		var run = new WorkflowRun {
			Id = runId,
			DocumentsDirectory = documentsDirectory,
			WorkflowId = definition.Id,
			SourcePath = sourcePath,
			SourceText = definition.SourceText,
			Inputs = inputs,
			Prompt = prompt,
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
			Hold = hold,
		};
		return Enter(run, definition.Stages[0].Id);
	}

	/// <summary>The agent reports the current stage done with <paramref name="outputs"/> and a <paramref name="summary"/>.</summary>
	public WorkflowCompletion Complete(WorkflowRun run, JsonElement outputs, string summary) {
		Require(run, WorkflowPhase.Open, "complete");
		var stage = StageOf(run.Stage);
		var errors = new List<string>();
		var values = ReadOutputs(stage, outputs, run.DocumentsDirectory, errors);
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
		// A question reached here rides the reply to this call, so the agent is owed no turn for it.
		var step = Advance(run, stage);
		return new WorkflowCompletion(step.Run.Phase != WorkflowPhase.Asking ? step : new WorkflowStep(
			step.Run with { Kickoff = null }, [.. step.Effects.Where(e => e is not WorkflowEffect.Kickoff)]), []);
	}

	/// <summary>The agent can't finish the current stage.</summary>
	public WorkflowStep Fail(WorkflowRun run, string reason) {
		Require(run, WorkflowPhase.Open, "fail");
		return Pause(run with { Kickoff = null }, $"The agent couldn't finish stage \"{run.Stage}\": {reason}");
	}

	/// <summary>The current stage couldn't open, so its turn was never sent; the run pauses with <paramref name="reason"/>.</summary>
	public WorkflowStep Halt(WorkflowRun run, string reason) {
		Require(run, WorkflowPhase.Open, "open");
		return Pause(run, reason);
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
	public WorkflowStep Resolve(WorkflowRun run, WorkflowDecisionResult result, double minimumConfidence) {
		Require(run, WorkflowPhase.Deciding, "resolve a decision for");
		var choices = run.Asked?.Choices ?? [];
		if (result is WorkflowDecisionResult.Refused refusal) {
			return AskUser(run, $"the decision model refused ({refusal.Reason})");
		}
		if (result is WorkflowDecisionResult.Failed failure) {
			return AskUser(run, $"the decision failed ({failure.Reason})");
		}
		if (result is not WorkflowDecisionResult.Answered answer || !choices.Contains(answer.Choice)
			|| !double.IsFinite(answer.Confidence) || answer.Confidence is < 0 or > 1) {
			return AskUser(run, "the decision model returned an invalid choice or confidence");
		}
		if (answer.Confidence < minimumConfidence) {
			return AskUser(run, FormattableString.Invariant($"the decision confidence {answer.Confidence} is below the required {minimumConfidence}"));
		}
		string choice = answer.Choice;

		if (run.Decision == WorkflowDecisionKind.Then) {
			var decide = (WorkflowThen.Decide)StageOf(run.Stage).Then;
			int count = run.Streaks.GetValueOrDefault(run.Stage) + 1;
			if (decide.Limit is { } limit && count > limit) {
				return AskUser(run, $"the decision model has answered this {limit} times in a row, the limit");
			}

			run = run with { Streaks = new() { [run.Stage] = count } };
		}

		return Route(run, choice, $"{words.Host} decided \"{choice}\"", "");
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
			+ $" Revise the work accordingly, then call {words.CompleteStage} again.");
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
			? Ask(run, WorkflowDecisionKind.If, condition, [new(Yes, null), new(No, null)], new Dictionary<string, string>())
			: Open(run, stage);
	}

	private WorkflowStep Advance(WorkflowRun run, WorkflowStage stage) => stage.Then switch {
		WorkflowThen.Goto go => Enter(run, go.Target),
		WorkflowThen.Decide decide => Ask(
			run, WorkflowDecisionKind.Then, decide.Question, [.. decide.Choices.Select(c => new WorkflowDecisionOption(c.Name, c.Description))],
			run.Outputs.GetValueOrDefault(stage.Id, []).ToDictionary(o => o.Key, o => o.Value.Render())),
		_ => Enter(run, Following(stage)),
	};

	// Opens the stage with its rendered work, prefixed by any feedback the user's last answer left for it.
	private WorkflowStep Open(WorkflowRun run, WorkflowStage stage) {
		if (!TryRender(run, stage.Run.Prompt ?? stage.Run.Args, out string rendered, out string missing)) {
			return Pause(run with { Stage = stage.Id }, missing);
		}

		// A conversation keeps the prompt's images once it has them, so only the first turn to use the prompt carries them.
		bool images = !run.PromptImagesSent && run.Prompt.Images.Count > 0
			&& (stage.Run.Prompt ?? stage.Run.Args)?.References.Any(r => r.IsPrompt) == true;
		var kickoff = (stage.Run.Skill is { } skill
			? new WorkflowKickoff { Text = $"/{skill} {Join(rendered, run.Note)}".TrimEnd(), Command = skill, Opens = stage.Id }
			: WorkflowKickoff.Prompt(Join(run.Note, rendered)) with { Opens = stage.Id }) with { CarriesPromptImages = images };
		return new WorkflowStep(
			run with { Stage = stage.Id, Phase = WorkflowPhase.Open, Decision = null, Asked = null, Attempts = 0, Note = "", Kickoff = kickoff },
			[new WorkflowEffect.Kickoff(kickoff)]);
	}

	private WorkflowStep Ask(
		WorkflowRun run, WorkflowDecisionKind kind, WorkflowQuestion question, IReadOnlyList<WorkflowDecisionOption> choices,
		IReadOnlyDictionary<string, string> outputs) {
		if (!TryRender(run, question.Ask, out string rendered, out string missing)) {
			return Pause(run with { Decision = kind }, missing);
		}

		run = run with {
			Decision = kind,
			Asked = new WorkflowAsked { Question = rendered, Choices = [.. choices.Select(c => c.Name)], Reason = "" },
		};
		if (question.By == WorkflowAnswerer.User) {
			return AskUser(run, "");
		}
		if (!TrySelectFiles(run, question.Ask, out var paths, out string error)) {
			return AskUser(run, $"the decision failed ({error})");
		}

		return new WorkflowStep(
			run with { Phase = WorkflowPhase.Deciding },
			[new WorkflowEffect.Decide(new WorkflowDecisionPlan(
				rendered, [.. choices], new Dictionary<string, string>(outputs),
				run.Note.Length == 0 ? run.Summary : Join(run.Summary, $"User feedback: {run.Note}"),
				paths, run.DocumentsDirectory, question.Confidence ?? decisionConfidence))]);
	}

	// Puts the run's current question to the user through the agent; a non-empty reason says why the model didn't answer it.
	private WorkflowStep AskUser(WorkflowRun run, string reason) {
		var asked = run.Asked! with { Reason = reason };
		var kickoff = WorkflowKickoff.Prompt(WorkflowStageBlock.PutToUser(asked, words, openReviewItem));
		return new WorkflowStep(
			run with { Phase = WorkflowPhase.Asking, Asked = asked, Kickoff = kickoff },
			[new WorkflowEffect.Kickoff(kickoff)]);
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
