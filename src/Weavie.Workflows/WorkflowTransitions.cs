using System.Text.Json;

namespace Weavie.Workflows;

/// <summary>
/// The pure state machine of a run: each transition takes a run and returns the next run plus the effects its
/// owner must perform. Nothing here touches files, threads, or the agent.
/// </summary>
internal sealed partial class WorkflowTransitions(WorkflowDefinition definition, WorkflowEnvironment environment) {
	private const string Yes = "yes";
	private const string No = "no";

	/// <summary>A new run of the definition from <paramref name="start"/>, entering its first stage.</summary>
	public WorkflowStep Start(WorkflowRunStart start) {
		var root = NewFrame($"{start.RunId}/0", "", "", 0, "", null);
		var run = new WorkflowRun {
			Id = start.RunId,
			DocumentsDirectory = start.DocumentsDirectory,
			WorkflowId = definition.Id,
			SourcePath = start.SourcePath,
			SourceText = definition.SourceText,
			Inputs = start.Inputs,
			Prompt = start.Prompt,
			Roles = start.Roles,
			Stage = definition.Stages[0].Id,
			Frame = root.Id,
			Frames = [root],
			NextFrame = 1,
			NextTurn = 1,
			Lost = null,
			Phase = WorkflowPhase.Open,
			Decision = null,
			Asked = null,
			Attempts = 0,
			Summary = "",
			Note = "",
			PausedReason = null,
			Kickoff = null,
			Hold = start.Hold,
		};
		return Enter(run, root, definition.Stages[0].Id);
	}

	/// <summary>The agent reports the current stage done with <paramref name="outputs"/> and a <paramref name="summary"/>.</summary>
	public WorkflowCompletion Complete(WorkflowRun run, JsonElement outputs, string summary) {
		Require(run, WorkflowPhase.Open, "complete");
		var stage = definition.Stage(run.Stage);
		var errors = new List<string>();
		var values = ReadOutputs(stage, outputs, Files(run), errors);
		if (errors.Count > 0) {
			int attempts = run.Attempts + 1;
			return new WorkflowCompletion(attempts >= environment.MaxStageAttempts()
				? Pause(run with { Attempts = attempts }, $"Stage \"{stage.Id}\" was rejected {attempts} times: {string.Join("; ", errors)}")
				: new WorkflowStep(run with { Attempts = attempts }, []), errors);
		}

		bool inMain = TurnConversation(run) is null;
		var live = Live(run);
		var entry = live.Stage == stage.Id && live.Entry > 0 ? live : null;
		var sits = Scope(run);
		run = Report(run with { Summary = summary, Attempts = 0, Kickoff = null }, stage, entry, sits, values, summary);
		var step = entry is not null ? EndEntry(run, run.FrameOf(entry.Id)!, values) : Advance(Store(run, sits, stage, values), run.FrameOf(sits.Id)!, stage);
		// A main-conversation caller receives its question in the completion reply; independent work reports to main.
		return new WorkflowCompletion(!inMain || step.Run.Phase != WorkflowPhase.Asking ? step : new WorkflowStep(
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

	/// <summary>The current visit lost its conversation; its pending work or decision can resume.</summary>
	public WorkflowStep ConversationEnded(WorkflowRun run, string reason) =>
		Pause(run, $"Stage \"{run.Stage}\" lost its conversation: {reason}");

	/// <summary>
	/// The conversation <paramref name="lost"/>, which its stages had already used, can't be restored; the run pauses, and
	/// Resume continues in a new one.
	/// </summary>
	public WorkflowStep ConversationLost(WorkflowRun run, WorkflowConversationRef lost, string reason) {
		var owner = run.FrameOf(lost.Frame);
		var entry = owner is { Entry: > 0 } && owner.Stage == lost.Opener ? owner : null;
		string position = Position(run, lost.Opener, entry, entry is null ? owner : run.FrameOf(entry.Parent));
		return Pause(run with { Lost = lost },
			$"Stage {position} lost its conversation: {reason}. Its stages' earlier work there is gone. Resume to continue in a new one.");
	}

	/// <summary>The user answers question <paramref name="question"/> with <paramref name="choice"/> and <paramref name="feedback"/> (empty for none).</summary>
	public WorkflowStep Answer(WorkflowRun run, int question, string choice, string feedback) {
		Require(run, WorkflowPhase.Asking, "answer");
		if (run.Asked!.Id != question) {
			throw new WorkflowException($"Question {question} isn't the one run {run.Id} is asking; it is asking question {run.Asked.Id}: {run.Asked.Question}");
		}
		if (!run.Asked.Choices.Contains(choice)) {
			throw new WorkflowException($"\"{choice}\" isn't an answer to this question; answer one of {string.Join(", ", run.Asked.Choices)}.");
		}

		return Route(run with { Frames = [.. run.Frames.Select(frame => frame with { Streaks = [] })] }, choice, $"The user answered \"{choice}\"", feedback);
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
			var decide = (WorkflowThen.Decide)definition.Stage(run.Stage).Then;
			var scope = Scope(run);
			int count = scope.Streaks.GetValueOrDefault(run.Stage) + 1;
			if (decide.Limit is { } limit && count > limit) {
				return AskUser(run, $"the decision model has answered this {limit} times in a row, the limit");
			}

			// A decision's count restarts any other in its list and every count inside it, never one outside it.
			var inside = Below(run, scope.Id).Select(frame => frame.Id).ToHashSet(StringComparer.Ordinal);
			run = run with {
				Frames = [.. run.Frames.Select(frame => frame.Id == scope.Id ? frame with { Streaks = new() { [run.Stage] = count } }
				: inside.Contains(frame.Id) ? frame with { Streaks = [] } : frame)]
			};
		}

		return Route(run, choice, $"{environment.Words.Host} decided \"{choice}\"", "");
	}

	/// <summary>The user resumes a paused run: a paused decision is asked again, otherwise the stage restarts.</summary>
	public WorkflowStep Resume(WorkflowRun run) {
		Require(run, WorkflowPhase.Paused, "resume");
		var stage = definition.Stage(run.Stage);
		var lost = run.Lost;
		run = run with { PausedReason = null, Attempts = 0, Lost = null };
		var scope = Scope(run);
		var live = Live(run);
		var step = run.Decision switch {
			WorkflowDecisionKind.If => Enter(run, scope, stage.Id, startsExecution: false),
			WorkflowDecisionKind.Then => Advance(run, scope, stage),
			_ when stage.Run is null || stage.Repeats && !(live.Stage == stage.Id && live.Entry > 0) => Begin(run, scope, stage),
			_ => Open(run, live, stage),
		};
		if (lost is null || step.Run.Kickoff is not { } kickoff) return step;
		var renewed = kickoff with { Renews = lost };
		return new(step.Run with { Kickoff = renewed }, [.. step.Effects.Select(e => e is WorkflowEffect.Kickoff ? new WorkflowEffect.Kickoff(renewed) : e)]);
	}

	/// <summary>The user moves the run to <paramref name="stageId"/>, a stage beside the current one or done; it opens regardless of its <c>if</c>.</summary>
	public WorkflowStep Jump(WorkflowRun run, string stageId) {
		if (run.IsFinished) {
			throw new WorkflowException($"Run {run.Id} has ended.");
		}

		var current = definition.Stage(run.Stage);
		var siblings = definition.List(current);
		if (stageId != WorkflowSchema.Done && siblings.All(s => s.Id != stageId)) {
			throw new WorkflowException($"Stage \"{stageId}\" isn't beside stage \"{current.Id}\"; jump to one of {string.Join(", ", siblings.Select(s => s.Id))}, or done.");
		}

		var scope = Scope(run);
		var entries = run.Frames.Where(f => f.Parent == scope.Id && f.Stage == current.Id && f.Entry > 0).Select(f => f.Id).ToList();
		run = Discard(run with { PausedReason = null, Lost = null, Decision = null, Asked = null, Kickoff = null }, entries);
		run = Update(run with { Frame = scope.Id }, scope.Id, frame => frame with { Loops = Without(frame.Loops, current.Id) });
		var step = stageId == WorkflowSchema.Done ? Finish(run, Scope(run)) : Begin(run, Scope(run), definition.Stage(stageId));
		return step with { Effects = [new WorkflowEffect.EnterStage(), .. step.Effects] };
	}

	/// <summary>The user cancels the run; needs no definition, so a run whose saved definition no longer reads can end.</summary>
	public static WorkflowStep Cancel(WorkflowRun run) {
		if (run.IsFinished) {
			throw new WorkflowException($"Run {run.Id} has ended.");
		}

		run = run with { Phase = WorkflowPhase.Cancelled, Decision = null, Asked = null, Note = "", Kickoff = null };
		return OwedTo(run, null).Length == 0 ? new WorkflowStep(run, [])
			: Send(run, WorkflowKickoff.Prompt($"Workflow {run.WorkflowId} cancelled."), "");
	}

	/// <summary>The run with <paramref name="turn"/> claimed: owed reports it carries delivered, its conversation used.</summary>
	public static WorkflowRun Claim(WorkflowRun run, WorkflowKickoff turn) {
		run = run with {
			Kickoff = null,
			PromptImagesSent = run.PromptImagesSent || turn.Conversation is null && turn.CarriesPromptImages,
		};
		run = ClearOwed(run, turn.Conversation);
		return turn.Conversation is { } used && turn.Opens.Length > 0
			? Update(run, used.Frame, frame => frame.Opened.Contains(used.Opener) ? frame : frame with { Opened = [.. frame.Opened, used.Opener] })
			: run;
	}

	// Where an answer leads. Back to a stage with work reopens it with a short turn; anywhere else enters that stage.
	private WorkflowStep Route(WorkflowRun run, string choice, string answered, string feedback) {
		var stage = definition.Stage(run.Stage);
		var scope = Scope(run);
		if (feedback.Length > 0) {
			run = run with { Note = Join(run.Note, $"{answered}: {feedback}") };
		}

		if (run.Decision == WorkflowDecisionKind.If) {
			run = run with { Decision = null, Asked = null };
			return choice == Yes ? Begin(run, scope, stage) : Enter(run, scope, Following(stage));
		}

		string target = ((WorkflowThen.Decide)stage.Then).Choices.First(c => c.Name == choice).Target;
		if (target != stage.Id || stage.Run is null || stage.Repeats) {
			return Enter(run, scope, target);
		}

		var kickoff = WorkflowKickoff.Prompt(
			$"{answered} to \"{run.Asked?.Question ?? ""}\", so stage \"{stage.Id}\" continues."
			+ (run.Note.Length == 0 ? "" : $" Feedback: {run.Note}")
			+ $" Revise the work accordingly, then call {environment.Words.CompleteStage} again.");
		var revised = Send(run with { Phase = WorkflowPhase.Open, Decision = null, Asked = null, Attempts = 0, Note = "" }, StageTurn(run, scope, stage, kickoff));
		return revised with { Effects = [new WorkflowEffect.ReviseStage(), .. revised.Effects] };
	}

	private WorkflowStep Enter(WorkflowRun run, WorkflowFrame frame, string target) => Enter(run, frame, target, startsExecution: true);

	private WorkflowStep Enter(WorkflowRun run, WorkflowFrame frame, string target, bool startsExecution) {
		run = run with { Frame = frame.Id, Decision = null, Asked = null, PausedReason = null, Attempts = 0, Kickoff = null };
		if (target == WorkflowSchema.Done) {
			return Finish(run, frame);
		}

		var stage = definition.Stage(target);
		run = run with { Stage = stage.Id };
		var step = stage.If is { } condition
			? Ask(run, WorkflowDecisionKind.If, condition, [new(Yes, null), new(No, null)], new Dictionary<string, string>())
			: Begin(run, frame, stage);
		return startsExecution ? step with { Effects = [new WorkflowEffect.EnterStage(), .. step.Effects] } : step;
	}

	private WorkflowStep Advance(WorkflowRun run, WorkflowFrame frame, WorkflowStage stage) {
		run = run with { Frame = frame.Id, Stage = stage.Id };
		return stage.Then switch {
			WorkflowThen.Goto go => Enter(run, frame, go.Target),
			WorkflowThen.Decide decide => Ask(
				run, WorkflowDecisionKind.Then, decide.Question, [.. decide.Choices.Select(c => new WorkflowDecisionOption(c.Name, c.Description))],
				run.FrameOf(frame.Id)!.Outputs.GetValueOrDefault(stage.Id, []).ToDictionary(o => o.Key, o => o.Value.Render())),
			_ => Enter(run, frame, Following(stage)),
		};
	}

	// Opens the stage's turn in record frame, prefixed by any feedback the user's last answer left for it.
	private WorkflowStep Open(WorkflowRun run, WorkflowFrame frame, WorkflowStage stage) {
		run = run with { Frame = frame.Id, Stage = stage.Id };
		var work = stage.Run!;
		if (!TryRender(run, work.Prompt ?? work.Args, out string rendered, out string missing)) {
			return Pause(run, missing);
		}

		var kickoff = work.Skill is { } skill
			? WorkflowKickoff.Prompt($"/{skill} {Join(rendered, run.Note)}".TrimEnd()) with { Command = skill, Opens = stage.Id }
			: WorkflowKickoff.Prompt(Join(run.Note, rendered)) with { Opens = stage.Id };
		return Send(run with { Phase = WorkflowPhase.Open, Decision = null, Asked = null, Attempts = 0, Note = "" },
			StageTurn(run, run.FrameOf(frame.Id)!, stage, kickoff));
	}

	private static WorkflowKickoff StageTurn(WorkflowRun run, WorkflowFrame frame, WorkflowStage stage, WorkflowKickoff turn) {
		var conversation = TurnConversation(frame, stage);
		return turn with {
			Conversation = conversation,
			Continues = conversation is not null && run.FrameOf(conversation.Frame)?.Opened.Contains(conversation.Opener) == true,
			CarriesPromptImages = (conversation is not null || !run.PromptImagesSent)
				&& run.Prompt.Images.Count > 0 && (stage.Run!.Prompt ?? stage.Run.Args)?.References.Any(reference => reference.IsPrompt) == true,
		};
	}

	private WorkflowStep Ask(
		WorkflowRun run, WorkflowDecisionKind kind, WorkflowQuestion question, IReadOnlyList<WorkflowDecisionOption> choices,
		IReadOnlyDictionary<string, string> outputs) {
		if (!TryRender(run, question.Ask, out string rendered, out string missing)) {
			return Pause(run with { Decision = kind }, missing);
		}

		run = run with {
			Decision = kind,
			Asked = new WorkflowAsked { Id = run.NextTurn, Question = rendered, Choices = [.. choices.Select(c => c.Name)], Reason = "" },
			NextTurn = run.NextTurn + 1,
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
				paths, Files(run), question.Confidence ?? environment.DecisionConfidence()))]);
	}

	// Puts the run's current question to the user through main; a non-empty reason says why the model didn't answer it.
	// A stage whose report went to another conversation brings its position, outputs, and summary with it.
	private WorkflowStep AskUser(WorkflowRun run, string reason) {
		var asked = run.Asked! with { Reason = reason };
		string context = run.Decision == WorkflowDecisionKind.Then && Scope(run).Conversation is not null
			? ReportText(run, definition.Stage(run.Stage), null, Scope(run), Scope(run).Outputs.GetValueOrDefault(run.Stage, []), run.Summary, own: false)
			: "";
		var kickoff = WorkflowKickoff.Prompt(Join(context, WorkflowStageBlock.PutToUser(asked, environment)));
		return Send(run with { Phase = WorkflowPhase.Asking, Asked = asked }, kickoff);
	}

	// A new fork also carries what is owed to the conversation it copies, which has not taken it yet.
	private WorkflowStep Send(WorkflowRun run, WorkflowKickoff kickoff) => Send(run, kickoff,
		kickoff.Conversation is { } conversation && !kickoff.Continues && definition.Stage(conversation.Opener).Context == WorkflowContext.Fork
			? OwedTo(run, Source(run, conversation)) : "");

	// Numbers the turn and prefixes what is owed to its conversation.
	private static WorkflowStep Send(WorkflowRun run, WorkflowKickoff kickoff, string inherited) {
		string owed = Join(inherited, OwedTo(run, kickoff.Conversation));
		if (owed.Length > 0) {
			kickoff = kickoff with { Text = kickoff.Command.Length == 0 ? Join(owed, kickoff.Text) : Join(kickoff.Text, owed) };
		}
		kickoff = kickoff with { Id = run.NextTurn };
		return new(run with { Kickoff = kickoff, NextTurn = run.NextTurn + 1 }, [new WorkflowEffect.Kickoff(kickoff)]);
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
