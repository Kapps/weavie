namespace Weavie.Workflows;

// The run's records: entering a stage with stages or a repeated stage opens one, finishing its list closes it.
internal sealed partial class WorkflowTransitions {
	// Starts a stage whose if (if any) said yes: a repetition, its stages, or its work.
	private WorkflowStep Begin(WorkflowRun run, WorkflowFrame frame, WorkflowStage stage) {
		frame = run.FrameOf(frame.Id)!;
		run = run with { Frame = frame.Id, Stage = stage.Id };
		if (stage.Context == WorkflowContext.Fork && stage.Reuse is null) {
			// A fork entered again copies its source again.
			run = Update(run, frame.Id, f => f with { Opened = [.. f.Opened.Where(o => o != stage.Id)] });
		}

		if (stage.Repeats) {
			if (!TryResolve(run, stage.Foreach!, out var list, out string missing)) return Pause(run, missing);
			var items = list.Flat();
			if (items.Count == 0) {
				return Enter(Store(run, frame, stage, stage.Outputs.ToDictionary(o => o.Name, _ => new WorkflowValue { Entries = [] })), frame, Following(stage));
			}

			run = Update(run, frame.Id, f => f with { Loops = new(f.Loops) { [stage.Id] = new() { Items = items, Results = [] } } });
			return StartEntry(run, frame, stage, 1);
		}

		if (stage.Stages.Count > 0) {
			var body = run.Frames.FirstOrDefault(f => f.Parent == frame.Id && f.Stage == stage.Id && f.Entry == 0);
			if (body is null) {
				(run, body) = AddFrame(run, frame, stage, 0, "");
			}
			return Enter(run, body, stage.Stages[0].Id);
		}

		return Open(run, frame, stage);
	}

	private WorkflowStep StartEntry(WorkflowRun run, WorkflowFrame parent, WorkflowStage stage, int entry) {
		parent = run.FrameOf(parent.Id)!;
		(run, var frame) = AddFrame(run, parent, stage, entry, parent.Loops[stage.Id].Items[entry - 1]);
		var step = stage.Stages.Count > 0 ? Enter(run, frame, stage.Stages[0].Id) : Open(run, frame, stage);
		return step with { Effects = [new WorkflowEffect.EnterStage(), .. step.Effects] };
	}

	// The record's list is done: the stage it is an instance of finishes with the outputs it copies.
	private WorkflowStep Finish(WorkflowRun run, WorkflowFrame frame) {
		frame = run.FrameOf(frame.Id)!;
		if (frame.Parent.Length == 0) {
			run = run with { Decision = null, Asked = null, PausedReason = null, Attempts = 0, Kickoff = null };
			if (OwedTo(run, null).Length > 0) return Send(run with { Phase = WorkflowPhase.Done, Note = "" },
				WorkflowKickoff.Prompt($"Workflow {definition.Id} finished."
					+ (run.Note.Length == 0 ? "" : $" User feedback: {run.Note}")));
			return new WorkflowStep(
				run with { Phase = WorkflowPhase.Done, Note = "", Kickoff = null },
				[new WorkflowEffect.Notify($"Workflow {definition.Id} finished."
					+ (run.Note.Length == 0 ? "" : $" Feedback with no stage left to receive it: {run.Note}"))]);
		}

		var stage = definition.Stage(frame.Stage);
		var values = new Dictionary<string, WorkflowValue>(StringComparer.Ordinal);
		foreach (var output in stage.Outputs) {
			if (frame.Outputs.GetValueOrDefault(output.Source!.Scope)?.GetValueOrDefault(output.Source.Name) is { } value) values[output.Name] = value;
		}

		var parent = run.FrameOf(frame.Parent)!;
		run = Report(run with { Frame = parent.Id, Stage = stage.Id }, stage, frame.Entry > 0 ? frame : null, parent, values, run.Summary);
		var step = frame.Entry > 0 ? EndEntry(run, frame, values) : Advance(Store(run, parent, stage, values), parent, stage);
		return step with { Effects = [new WorkflowEffect.EnterStage(), .. step.Effects] };
	}

	// An entry finished: its record and everything in it go, and the next entry starts or the repetition finishes.
	private WorkflowStep EndEntry(WorkflowRun run, WorkflowFrame entry, Dictionary<string, WorkflowValue> values) {
		entry = run.FrameOf(entry.Id)!;
		var stage = definition.Stage(entry.Stage);
		var loop = run.FrameOf(entry.Parent)!.Loops[stage.Id];
		loop = loop with { Results = [.. loop.Results, values] };
		run = Discard(run with { Frame = entry.Parent, Stage = stage.Id }, [entry.Id]);
		if (entry.Entry < loop.Items.Count) {
			run = Update(run, entry.Parent, f => f with { Loops = new(f.Loops) { [stage.Id] = loop } });
			return StartEntry(run, run.FrameOf(entry.Parent)!, stage, entry.Entry + 1);
		}

		var outputs = new Dictionary<string, WorkflowValue>(StringComparer.Ordinal);
		foreach (var output in stage.Outputs) {
			List<WorkflowEntryValue> entries = [.. loop.Results.Select((result, i) => (result, i))
				.Where(r => r.result.ContainsKey(output.Name))
				.Select(r => new WorkflowEntryValue { Item = loop.Items[r.i], Value = r.result[output.Name] })];
			if (entries.Count > 0) outputs[output.Name] = new WorkflowValue { Entries = entries };
		}
		run = Update(run, entry.Parent, f => f with { Loops = Without(f.Loops, stage.Id) });
		run = Store(run, run.FrameOf(entry.Parent)!, stage, outputs);
		var step = Advance(run, run.FrameOf(entry.Parent)!, stage);
		return step with { Effects = [new WorkflowEffect.EnterStage(), .. step.Effects] };
	}

	private static WorkflowRun Store(WorkflowRun run, WorkflowFrame frame, WorkflowStage stage, Dictionary<string, WorkflowValue> values) =>
		Update(run, frame.Id, f => f with { Outputs = new(f.Outputs) { [stage.Id] = values } });

	// A stage that ran in its own conversation reports to the one it sits in.
	private static WorkflowRun Report(
		WorkflowRun run, WorkflowStage stage, WorkflowFrame? entry, WorkflowFrame sits, Dictionary<string, WorkflowValue> values, string summary) {
		if (stage.Context == WorkflowContext.Continue) return run;
		string text = ReportText(run, stage, entry, sits, values, summary, own: true);
		var target = sits.Conversation;
		string frame = target?.Frame ?? run.Frames[0].Id;
		string key = target?.Opener ?? "";
		return Update(run, frame, f => f with { Owed = new(f.Owed) { [key] = Join(f.Owed.GetValueOrDefault(key, ""), text) } });
	}

	private static string ReportText(
		WorkflowRun run, WorkflowStage stage, WorkflowFrame? entry, WorkflowFrame sits, Dictionary<string, WorkflowValue> values, string summary, bool own) =>
		$"Stage {Position(run, stage.Id, entry, sits)} {(own ? "ran in its own conversation and completed" : "completed")}.\nOutputs: "
		+ (values.Count == 0 ? "{}" : string.Join("\n", values.Select(output => $"{output.Key} = {output.Value.Render()}")))
		+ $"\nSummary: {summary}";

	/// <summary>A stage's place for agent-facing text: <c>"task" (entry 2 of 3: "refunds"), inside "group"</c>.</summary>
	internal static string Position(WorkflowRun run, string stage, WorkflowFrame? entry, WorkflowFrame? sits) {
		var parts = new List<string> { $"\"{stage}\"{Entry(run, entry)}" };
		for (var frame = sits; frame is { Parent.Length: > 0 }; frame = run.FrameOf(frame.Parent)) {
			parts.Add($"inside \"{frame.Stage}\"{Entry(run, frame)}");
		}
		return string.Join(", ", parts);
	}

	private static string Entry(WorkflowRun run, WorkflowFrame? frame) => frame is not { Entry: > 0 } ? ""
		: $" (entry {frame.Entry} of {run.FrameOf(frame.Parent)?.Loops.GetValueOrDefault(frame.Stage)?.Items.Count}: \"{frame.Item.Split('\n')[0].Trim()}\")";

	/// <summary>The conversation the run's stage takes its turns in, or <see langword="null"/> for main.</summary>
	public WorkflowConversationRef? TurnConversation(WorkflowRun run) => TurnConversation(run, definition);

	/// <summary>The conversation <paramref name="run"/>'s stage takes its turns in, or <see langword="null"/> for main.</summary>
	public static WorkflowConversationRef? TurnConversation(WorkflowRun run, WorkflowDefinition definition) =>
		TurnConversation(Live(run), definition.Stage(run.Stage));

	/// <summary>Where <paramref name="run"/> is, for agent-facing text.</summary>
	public static string Position(WorkflowRun run) {
		var live = Live(run);
		return live.Stage == run.Stage && live.Entry > 0 ? Position(run, run.Stage, live, run.FrameOf(live.Parent)) : Position(run, run.Stage, null, live);
	}

	private static WorkflowConversationRef? TurnConversation(WorkflowFrame frame, WorkflowStage stage) =>
		frame.Stage == stage.Id && frame.Entry > 0 ? frame.Conversation : Own(frame, null, stage);

	// The conversation a stage sitting in record sits opens or uses; entry is its own record when it repeats.
	private static WorkflowConversationRef? Own(WorkflowFrame sits, WorkflowFrame? entry, WorkflowStage stage) =>
		stage.Reuse is { } reused ? new(sits.Id, reused)
		: stage.Context != WorkflowContext.Continue ? new((entry ?? sits).Id, stage.Id)
		: sits.Conversation;

	/// <summary>The conversation <paramref name="conversation"/> is a fork of: the one its opening stage sits in.</summary>
	public static WorkflowConversationRef? Source(WorkflowRun run, WorkflowConversationRef conversation) {
		var owner = run.FrameOf(conversation.Frame);
		return owner is null ? null
			: owner.Stage == conversation.Opener && owner.Entry > 0 ? run.FrameOf(owner.Parent)?.Conversation
			: owner.Conversation;
	}

	private static string OwedTo(WorkflowRun run, WorkflowConversationRef? conversation) =>
		conversation is null ? run.Frames[0].Owed.GetValueOrDefault("", "")
		: run.FrameOf(conversation.Frame)?.Owed.GetValueOrDefault(conversation.Opener, "") ?? "";

	private static WorkflowRun ClearOwed(WorkflowRun run, WorkflowConversationRef? conversation) {
		string frame = conversation?.Frame ?? run.Frames[0].Id;
		string key = conversation?.Opener ?? "";
		return Update(run, frame, f => f.Owed.ContainsKey(key) ? f with { Owed = Without(f.Owed, key) } : f);
	}

	private static (WorkflowRun, WorkflowFrame) AddFrame(WorkflowRun run, WorkflowFrame parent, WorkflowStage stage, int entry, string item) {
		string id = $"{run.Id}/{run.NextFrame}";
		var frame = NewFrame(id, parent.Id, stage.Id, entry, item, null);
		frame = frame with { Conversation = Own(parent, entry > 0 ? frame : null, stage) };
		return (run with { Frames = [.. run.Frames, frame], NextFrame = run.NextFrame + 1 }, frame);
	}

	private static WorkflowFrame NewFrame(string id, string parent, string stage, int entry, string item, WorkflowConversationRef? conversation) => new() {
		Id = id,
		Parent = parent,
		Stage = stage,
		Entry = entry,
		Item = item,
		Conversation = conversation,
		Outputs = [],
		Loops = [],
		Streaks = [],
		Owed = [],
		Opened = [],
	};

	/// <summary>The innermost record the run is inside.</summary>
	internal static WorkflowFrame Live(WorkflowRun run) => run.FrameOf(run.Frame)!;

	/// <summary>The record whose list holds the run's stage.</summary>
	internal static WorkflowFrame Scope(WorkflowRun run) {
		var live = Live(run);
		return live.Stage == run.Stage && live.Entry > 0 ? run.FrameOf(live.Parent)! : live;
	}

	private static IEnumerable<WorkflowFrame> Below(WorkflowRun run, string id) {
		foreach (var child in run.Frames.Where(f => f.Parent == id)) {
			yield return child;
			foreach (var below in Below(run, child.Id)) yield return below;
		}
	}

	// Drops the records and everything below them.
	private static WorkflowRun Discard(WorkflowRun run, IEnumerable<string> ids) {
		var gone = ids.SelectMany(id => Below(run, id).Select(f => f.Id).Append(id)).ToHashSet(StringComparer.Ordinal);
		return gone.Count == 0 ? run : run with { Frames = [.. run.Frames.Where(f => !gone.Contains(f.Id))] };
	}

	private static WorkflowRun Update(WorkflowRun run, string id, Func<WorkflowFrame, WorkflowFrame> change) =>
		run with { Frames = [.. run.Frames.Select(frame => frame.Id == id ? change(frame) : frame)] };

	private static Dictionary<string, T> Without<T>(Dictionary<string, T> map, string key) {
		var copy = new Dictionary<string, T>(map, StringComparer.Ordinal);
		copy.Remove(key);
		return copy;
	}

	private string Following(WorkflowStage stage) {
		var list = definition.List(stage);
		int index = list.ToList().IndexOf(stage);
		return index + 1 < list.Count ? list[index + 1].Id : WorkflowSchema.Done;
	}
}
