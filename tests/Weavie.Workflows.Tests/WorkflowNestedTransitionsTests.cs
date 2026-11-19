using System.Text.Json;
using Xunit;

namespace Weavie.Workflows.Tests;

public sealed class WorkflowNestedTransitionsTests {
	private const string Tree = """
		id: tree
		description: Groups of tasks.
		agents:
		  opus: { provider: p, model: opus }
		  fable: { provider: p, model: fable }
		stages:
		  - id: design
		    run: { prompt: Design. }
		    outputs: { groups: list }
		  - id: group
		    foreach: "{{design.groups}}"
		    agent: opus
		    context: fork
		    outputs: { pr: "{{pr.pr}}", notes: "{{task.notes}}" }
		    stages:
		      - id: plan
		        run: { prompt: "Plan {{group.item}} ({{group.index}})." }
		        outputs: { tasks: list }
		      - id: task
		        foreach: "{{plan.tasks}}"
		        context: fork
		        run: { prompt: "Do {{task.item}} of {{group.item}}." }
		        outputs: { notes: text }
		      - id: review
		        agent: fable
		        context: fresh
		        run: { prompt: Review. }
		        outputs: { verdict: text }
		        then: { ask: "Is {{review.verdict}} clean?", choices: { clean: pr, fix: fix }, limit: 1 }
		      - id: fix
		        run: { prompt: Fix. }
		        then: review
		      - id: pr
		        run: { prompt: Open the PR. }
		        outputs: { pr: pr }
		        then: { ask: Ready?, by: user, choices: { ready: done, iterate: fix } }
		  - id: join
		    run: { prompt: "Join {{group.pr}}." }
		""";

	private static WorkflowDefinition Read(string text) {
		var read = WorkflowReader.ReadProposal(text);
		return read.Definition ?? throw new InvalidOperationException(string.Join("\n", read.Diagnostics));
	}

	private static readonly WorkflowTransitions Machine = TestWorkflows.Machine(Read(Tree));

	private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

	private static WorkflowRun Complete(WorkflowRun run, string outputs) {
		var completion = Machine.Complete(Claimed(run), Json(outputs), $"did {run.Stage}");
		Assert.Empty(completion.Errors);
		return completion.Step.Run;
	}

	private static WorkflowRun Claimed(WorkflowRun run) => run.Kickoff is { } kickoff ? WorkflowTransitions.Claim(run, kickoff) : run;

	private static WorkflowRun Decide(WorkflowRun run, string choice) =>
		Machine.Resolve(run, new WorkflowDecisionResult.Answered(choice, 1), 0.8).Run;

	// Runs one group entry from plan to an accepted pr: one task per item, a review that fixes once, then hits its limit.
	private static WorkflowRun Group(WorkflowRun run, string url, params string[] tasks) {
		run = Complete(run, JsonSerializer.Serialize(new { tasks }));
		foreach (string task in tasks) run = Complete(run, JsonSerializer.Serialize(new { notes = $"notes on {task}" }));
		run = Decide(Complete(run, """{"verdict":"two bugs"}"""), "fix");
		run = Decide(Complete(Complete(run, "{}"), """{"verdict":"clean"}"""), "clean");
		run = Machine.Answer(Claimed(run), "clean", "").Run;
		run = Complete(run, $$"""{"pr":"{{url}}"}""");
		return Machine.Answer(Claimed(run), "ready", "").Run;
	}

	[Fact]
	public void AGroupRunsItsStagesInItsForkAndEachTaskForksTheGroup() {
		var run = Complete(Machine.Start(TestWorkflows.Start).Run, """{"groups":["auth","billing"]}""");
		Assert.Equal(("plan", "group 1/2 › plan"), (run.Stage, run.Path()));
		var entry = run.FrameOf(run.Frame)!;
		Assert.Equal(("group", 1, "auth"), (entry.Stage, entry.Entry, entry.Item));
		var plan = run.Kickoff!;
		Assert.Equal((new WorkflowConversationRef(entry.Id, "group"), false), (plan.Conversation, plan.Continues));
		Assert.Equal("Plan auth (1).", plan.Text);

		run = Complete(run, """{"tasks":["schema","api"]}""");
		var task = run.Kickoff!;
		Assert.Equal("task", task.Conversation!.Opener);
		Assert.NotEqual(entry.Id, task.Conversation.Frame);
		Assert.Equal("group 1/2 › task 1/2", run.Path());
		Assert.Equal("Do schema of auth.", task.Text);

		run = Complete(run, """{"notes":"added the schema"}""");
		Assert.Equal("Do api of auth.", run.Kickoff!.Text[^"Do api of auth.".Length..]);
		Assert.Contains("Stage \"task\" (entry 1 of 2: \"schema\"), inside \"group\" (entry 1 of 2: \"auth\") ran in its own conversation", run.Kickoff.Text);
		Assert.Contains("notes = added the schema", run.Kickoff.Text);
		Assert.Single(run.Frames, f => f.Stage == "task");

		run = Complete(run, """{"notes":"added the api\nand its tests"}""");
		Assert.DoesNotContain(run.Frames, f => f.Stage == "task");
		var review = run.Kickoff!;
		Assert.Equal((new WorkflowConversationRef(entry.Id, "review"), false), (review.Conversation, review.Continues));
		Assert.Equal("Review.", review.Text);
		Assert.Equal("- schema: added the schema\n- api:\n  added the api\n  and its tests", run.FrameOf(entry.Id)!.Outputs["task"]["notes"].Render());
	}

	[Fact]
	public void LimitsCountPerEntryAndQuestionsReachMainWithTheirStage() {
		var run = Complete(Machine.Start(TestWorkflows.Start).Run, """{"groups":["auth","billing"]}""");
		run = Complete(run, """{"tasks":["schema"]}""");
		run = Complete(run, """{"notes":"done"}""");
		run = Decide(Complete(run, """{"verdict":"bug"}"""), "fix");
		Assert.True(run.Kickoff!.Continues);
		Assert.Equal("group", run.Kickoff.Conversation!.Opener);
		run = Complete(run, "{}");
		Assert.Equal((new WorkflowConversationRef(run.Frame, "review"), true), (run.Kickoff!.Conversation, run.Kickoff.Continues));
		var asked = Machine.Resolve(Complete(run, """{"verdict":"bug"}"""), new WorkflowDecisionResult.Answered("fix", 1), 0.8);
		Assert.Equal(WorkflowPhase.Asking, asked.Run.Phase);
		var question = asked.Run.Kickoff!;
		Assert.Null(question.Conversation);
		Assert.StartsWith("Stage \"review\", inside \"group\" (entry 1 of 2: \"auth\") completed.\nOutputs: verdict = bug", question.Text);
		Assert.Contains($"question {asked.Run.Asked!.Id}", question.Text);
		Assert.Throws<WorkflowException>(() => Machine.Answer(asked.Run, asked.Run.Asked.Id + 1, "clean", ""));

		run = Machine.Answer(asked.Run, asked.Run.Asked.Id, "clean", "").Run;
		run = Machine.Answer(Claimed(Complete(run, """{"pr":"https://example.com/pr/1"}""")), "ready", "").Run;
		Assert.Equal("group 2/2 › plan", run.Path());
		run = Complete(run, """{"tasks":["schema"]}""");
		run = Complete(run, """{"notes":"done"}""");
		run = Decide(Complete(run, """{"verdict":"bug"}"""), "fix");
		Assert.Equal("fix", run.Stage);
	}

	[Fact]
	public void FinishedEntriesCopyTheirOutputsAndReportToMain() {
		var run = Complete(Machine.Start(TestWorkflows.Start).Run, """{"groups":["auth","billing"]}""");
		string first = run.Frame;
		run = Group(run, "https://example.com/pr/1", "schema", "api");
		Assert.Null(run.FrameOf(first));
		Assert.NotEqual(first, run.Frame);
		Assert.Equal(("group", 2, "billing"), (run.FrameOf(run.Frame)!.Stage, run.FrameOf(run.Frame)!.Entry, run.FrameOf(run.Frame)!.Item));
		Assert.Contains("Stage \"group\" (entry 1 of 2: \"auth\") ran in its own conversation and completed.", run.Frames[0].Owed[""]);
		Assert.Contains("Plan billing (2).", run.Kickoff!.Text);
		Assert.Contains("ran in its own conversation", run.Kickoff.Text);

		run = Group(run, "https://example.com/pr/2", "invoices");
		Assert.Equal(["root"], run.Frames.Select(_ => "root"));
		Assert.Equal(("join", null), (run.Stage, run.Kickoff!.Conversation));
		Assert.EndsWith("Join - auth: https://example.com/pr/1\n- billing: https://example.com/pr/2.", run.Kickoff.Text);
		Assert.Contains("Stage \"group\" (entry 2 of 2: \"billing\") ran in its own conversation and completed.", run.Kickoff.Text);
		var notes = run.Frames[0].Outputs["group"]["notes"];
		Assert.Equal(["notes on schema", "notes on api", "notes on invoices"], notes.Flat());
		Assert.Equal("- auth:\n  - schema: notes on schema\n  - api: notes on api\n- billing:\n  - invoices: notes on invoices", notes.Render());
	}

	[Fact]
	public void AnEmptyListSkipsTheStageWithEmptyOutputs() {
		var run = Complete(Machine.Start(TestWorkflows.Start).Run, """{"groups":[]}""");
		Assert.Equal("join", run.Stage);
		Assert.Equal("Join .", run.Kickoff!.Text);
		Assert.Empty(run.Frames[0].Outputs["group"]["pr"].Entries!);
	}

	[Fact]
	public void JumpReachesSiblingsOrDoneAndDropsTheEntryItLeaves() {
		var run = Complete(Machine.Start(TestWorkflows.Start).Run, """{"groups":["auth","billing"]}""");
		run = Complete(run, """{"tasks":["schema","api"]}""");
		Assert.Throws<WorkflowException>(() => Machine.Jump(run, "join"));
		var review = Machine.Jump(run, "review");
		Assert.Contains(review.Effects, e => e is WorkflowEffect.EnterStage);
		Assert.Equal("group 1/2 › review", review.Run.Path());
		Assert.DoesNotContain(review.Run.Frames, f => f.Stage == "task");
		Assert.Empty(review.Run.FrameOf(review.Run.Frame)!.Loops);

		var done = Machine.Jump(review.Run, "done");
		Assert.Equal("group 2/2 › plan", done.Run.Path());
		Assert.Equal([], done.Run.Frames[0].Loops["group"].Results[0].Keys);
	}

	[Fact]
	public void ALostConversationPausesAndResumeOpensItAgain() {
		var run = Complete(Machine.Start(TestWorkflows.Start).Run, """{"groups":["auth"]}""");
		run = Complete(run, """{"tasks":["schema"]}""");
		run = Complete(run, """{"notes":"done"}""");
		run = Decide(Complete(run, """{"verdict":"bug"}"""), "fix");
		var group = run.Kickoff!.Conversation!;
		var lost = Machine.ConversationLost(run, group, "the agent ended");
		Assert.Equal(WorkflowPhase.Paused, lost.Run.Phase);
		Assert.Equal(
			"Stage \"group\" (entry 1 of 1: \"auth\") lost its conversation: the agent ended. Its stages' earlier work there is gone. Resume to continue in a new one.",
			lost.Run.PausedReason);
		var resumed = Machine.Resume(lost.Run).Run;
		Assert.Equal((group, group, "fix"), (resumed.Kickoff!.Conversation, resumed.Kickoff.Renews, resumed.Kickoff.Opens));
		Assert.Null(resumed.Lost);
	}

	[Fact]
	public void AStagesOwnDecisionCountSurvivesTheDecisionsInsideIt() {
		var definition = Read("""
			id: loop
			description: Loops.
			stages:
			  - id: round
			    stages:
			      - id: check
			        run: { prompt: Check. }
			        then: { ask: "Clean?", choices: { ok: done, redo: check } }
			    outputs: {}
			    then: { ask: "Again?", choices: { again: round, stop: done }, limit: 1 }
			""");
		var machine = TestWorkflows.Machine(definition);
		var run = machine.Start(TestWorkflows.Start).Run;
		WorkflowRun Pass(WorkflowRun at) {
			at = machine.Complete(WorkflowTransitions.Claim(at, at.Kickoff!), Json("{}"), "checked").Step.Run;
			return machine.Resolve(at, new WorkflowDecisionResult.Answered("ok", 1), 0.8).Run;
		}
		run = machine.Resolve(Pass(run), new WorkflowDecisionResult.Answered("again", 1), 0.8).Run;
		Assert.Equal(("check", 1), (run.Stage, run.Frames[0].Streaks["round"]));
		var asked = machine.Resolve(Pass(run), new WorkflowDecisionResult.Answered("again", 1), 0.8).Run;
		Assert.Equal((WorkflowPhase.Asking, "round"), (asked.Phase, asked.Stage));
		Assert.Contains("the limit", asked.Asked!.Reason);
	}

	[Fact]
	public void TheRunDocumentKeepsItsRecords() {
		var run = Complete(Complete(Machine.Start(TestWorkflows.Start).Run, """{"groups":["auth"]}"""), """{"tasks":["schema"]}""");
		var read = WorkflowRunDocument.Read(WorkflowRunDocument.Write(run))!;
		Assert.Equal(run.Frames.Select(f => (f.Id, f.Parent, f.Stage, f.Entry, f.Item, f.Conversation)),
			read.Frames.Select(f => (f.Id, f.Parent, f.Stage, f.Entry, f.Item, f.Conversation)));
		Assert.Equal(["schema"], read.Frames[1].Outputs["plan"]["tasks"].Items!);
		Assert.Equal(run.Kickoff, read.Kickoff);
		Assert.Equal(run.Path(), read.Path());
	}
}
