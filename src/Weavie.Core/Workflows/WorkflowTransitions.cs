using System.Text.Json;

namespace Weavie.Core.Workflows;

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
			Deciding = null,
			Attempts = 0,
			Outputs = [],
			EdgeCounts = [],
			PausedReason = null,
			Kickoff = null,
		};
		return Enter(run, definition.Stages[0].Id);
	}

	/// <summary>The agent reports the current stage done with <paramref name="outputs"/>.</summary>
	public WorkflowCompletion Complete(WorkflowRun run, JsonElement outputs) {
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
			Attempts = 0,
			Kickoff = null,
		};
		return new WorkflowCompletion(stage.Gate
			? new WorkflowStep(
				run with { Phase = WorkflowPhase.AwaitingApproval },
				[new WorkflowEffect.Notify($"Stage \"{stage.Id}\" of {definition.Id} is waiting for your approval.")])
			: Advance(run, stage), []);
	}

	/// <summary>The agent can't finish the current stage.</summary>
	public WorkflowStep Fail(WorkflowRun run, string reason) {
		Require(run, WorkflowPhase.Open, "fail");
		return Pause(run with { Kickoff = null }, $"The agent couldn't finish stage \"{run.Stage}\": {reason}");
	}

	/// <summary>The user approves the stage awaiting approval.</summary>
	public WorkflowStep Approve(WorkflowRun run) {
		Require(run, WorkflowPhase.AwaitingApproval, "approve");
		return Advance(run, StageOf(run.Stage));
	}

	/// <summary>The user rejects the stage awaiting approval; the stage reopens with their feedback.</summary>
	public WorkflowStep Reject(WorkflowRun run, string feedback) {
		Require(run, WorkflowPhase.AwaitingApproval, "reject");
		string kickoff = $"I didn't approve stage \"{run.Stage}\" yet: {feedback}\n\nRevise the work, then call completeStage again.";
		return new WorkflowStep(run with { Phase = WorkflowPhase.Open, Attempts = 0, Kickoff = kickoff }, [new WorkflowEffect.Kickoff(kickoff)]);
	}

	/// <summary>A decision answered (or failed).</summary>
	public WorkflowStep Resolve(WorkflowRun run, WorkflowDecisionResult result) {
		Require(run, WorkflowPhase.Deciding, "resolve a decision for");
		var stage = StageOf(run.Stage);
		var choices = run.Deciding == WorkflowDecisionKind.When
			? [new WorkflowChoice(Yes, stage.Id), new WorkflowChoice(No, stage.Else ?? Following(stage))]
			: ((WorkflowNext.Choose)stage.Next).Choices;
		if (result.Choice is not { } choice) {
			return Pause(run, $"Couldn't decide what follows stage \"{stage.Id}\": {result.Failure}");
		}

		if (choice == WorkflowDecision.Unsure) {
			return Pause(run, $"The decision model was unsure at stage \"{stage.Id}\". Resume to ask again, or jump to the stage you want.");
		}

		if (choices.FirstOrDefault(c => c.Name == choice) is not { } chosen) {
			return Pause(run, $"The decision at stage \"{stage.Id}\" answered \"{choice}\", which isn't one of its choices.");
		}

		if (run.Deciding == WorkflowDecisionKind.When) {
			return choice == Yes ? Open(run with { Deciding = null }, stage) : Enter(run, chosen.Target);
		}

		string edge = $"{stage.Id}→{chosen.Target}";
		int count = run.EdgeCounts.GetValueOrDefault(edge) + 1;
		run = run with { EdgeCounts = new(run.EdgeCounts) { [edge] = count } };
		return ((WorkflowNext.Choose)stage.Next).Max is { } max && count > max
			? Pause(run, $"Stage \"{stage.Id}\" chose \"{chosen.Target}\" more than its limit of {max}. Resume to continue anyway, or jump to a stage.")
			: Enter(run, chosen.Target);
	}

	/// <summary>The user resumes a paused run: a paused decision is asked again, otherwise the stage restarts.</summary>
	public WorkflowStep Resume(WorkflowRun run) {
		Require(run, WorkflowPhase.Paused, "resume");
		var stage = StageOf(run.Stage);
		run = run with { PausedReason = null, Attempts = 0 };
		return run.Deciding switch {
			WorkflowDecisionKind.When => Enter(run, stage.Id),
			WorkflowDecisionKind.Next => Advance(run with { EdgeCounts = [] }, stage),
			_ => Open(run, stage),
		};
	}

	/// <summary>The user moves the run to <paramref name="stageId"/>, which opens regardless of its <c>when</c>.</summary>
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

		return new WorkflowStep(run with { Phase = WorkflowPhase.Cancelled, Deciding = null, Kickoff = null }, []);
	}

	private WorkflowStep Enter(WorkflowRun run, string target) {
		run = run with { Deciding = null, PausedReason = null, Attempts = 0, Kickoff = null };
		if (target == WorkflowSchema.Done) {
			return new WorkflowStep(
				run with { Phase = WorkflowPhase.Done },
				[new WorkflowEffect.Notify($"Workflow {definition.Id} finished.")]);
		}

		var stage = StageOf(target);
		run = run with { Stage = stage.Id };
		if (stage.When is not { } when) {
			return Open(run, stage);
		}

		return Decide(run, WorkflowDecisionKind.When, when, [Yes, No], new Dictionary<string, string>());
	}

	private WorkflowStep Advance(WorkflowRun run, WorkflowStage stage) => stage.Next switch {
		WorkflowNext.Goto go => Enter(run, go.Target),
		WorkflowNext.Choose choose => Decide(
			run, WorkflowDecisionKind.Next, choose.Prompt, [.. choose.Choices.Select(c => c.Name)],
			run.Outputs.GetValueOrDefault(stage.Id, []).ToDictionary(o => o.Key, o => o.Value.Render())),
		_ => Enter(run, Following(stage)),
	};

	private WorkflowStep Open(WorkflowRun run, WorkflowStage stage) {
		if (!TryRender(run, stage.Do.Prompt ?? stage.Do.Args, out string rendered, out string missing)) {
			return Pause(run with { Stage = stage.Id }, missing);
		}

		string kickoff = stage.Do.Skill is { } skill ? $"/{skill} {rendered}".TrimEnd() : rendered;
		return new WorkflowStep(
			run with { Stage = stage.Id, Phase = WorkflowPhase.Open, Deciding = null, Attempts = 0, Kickoff = kickoff },
			[new WorkflowEffect.Kickoff(kickoff)]);
	}

	private WorkflowStep Decide(
		WorkflowRun run, WorkflowDecisionKind kind, WorkflowTemplate question, IReadOnlyList<string> choices,
		IReadOnlyDictionary<string, string> outputs) {
		if (!TryRender(run, question, out string rendered, out string missing)) {
			return Pause(run, missing);
		}

		return new WorkflowStep(
			run with { Phase = WorkflowPhase.Deciding, Deciding = kind },
			[new WorkflowEffect.Decide(new WorkflowDecisionRequest { Question = rendered, Choices = choices, Outputs = outputs })]);
	}

	private static WorkflowStep Pause(WorkflowRun run, string reason) => new(
		run with { Phase = WorkflowPhase.Paused, PausedReason = reason, Kickoff = null },
		[new WorkflowEffect.Notify($"Workflow {run.WorkflowId} paused: {reason}")]);

	private static void Require(WorkflowRun run, WorkflowPhase phase, string action) {
		if (run.Phase != phase) {
			throw new WorkflowException($"Can't {action} stage \"{run.Stage}\" of run {run.Id}: it is {Describe(run.Phase)}.");
		}
	}

	private static string Describe(WorkflowPhase phase) => phase switch {
		WorkflowPhase.Open => "open",
		WorkflowPhase.AwaitingApproval => "awaiting approval",
		WorkflowPhase.Deciding => "deciding what comes next",
		WorkflowPhase.Paused => "paused",
		WorkflowPhase.Done => "done",
		_ => "cancelled",
	};
}
