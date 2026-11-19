namespace Weavie.Workflows;

// Decisions: asking the model or the user, and where each answer leads.
internal sealed partial class WorkflowTransitions {
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
			? ReportText(run, definition.Stage(run.Stage), null, Scope(run), Scope(run).Outputs.GetValueOrDefault(run.Stage, []), run.Summary)
			: "";
		var kickoff = WorkflowKickoff.Prompt(Join(context, WorkflowStageBlock.PutToUser(asked, environment)));
		return Send(run with { Phase = WorkflowPhase.Asking, Asked = asked }, kickoff);
	}
}
