using Xunit;

namespace Weavie.Workflows.Tests;

public sealed class WorkflowTreeSemanticsTests {
	private static string Document(string stages) => $"id: x\ndescription: d\nagents: {{ a: {{ provider: p }} }}\nstages:\n{stages}";

	private static IReadOnlyList<WorkflowDiagnostic> Errors(string stages) =>
		[.. WorkflowReader.Read(Document(stages), "x").Diagnostics.Where(d => !d.IsWarning)];

	[Theory]
	[InlineData("  - id: g\n    run: { prompt: p }\n    stages: [{ id: c, run: { prompt: p } }]", "stages[0]", "a stage has exactly one of run or stages")]
	[InlineData("  - id: g\n    stages: [{ id: g, run: { prompt: p } }]", "stages[0].stages[0].id", "stage id \"g\" is used twice")]
	[InlineData("  - id: a1\n    run: { prompt: p }\n    outputs: { n: number }", "stages[0].outputs.n", "\"number\" is not a type")]
	[InlineData("  - id: g\n    stages: [{ id: c, run: { prompt: p }, outputs: { n: text } }]\n    outputs: { n: text }", "stages[0].outputs.n", "takes each output from one of its stages")]
	[InlineData("  - id: g\n    stages: [{ id: c, run: { prompt: p }, outputs: { n: text } }]\n    outputs: { n: \"{{c.m}}\" }", "stages[0].outputs.n", "{{c.m}} names no output of a stage inside \"g\"")]
	[InlineData("  - id: l\n    run: { prompt: p }\n    outputs: { items: list }\n  - id: r\n    foreach: \"{{l.items}}\"\n    run: { prompt: p }\n    outputs: { item: text }", "stages[1].outputs.item", "\"item\" names the current entry")]
	[InlineData("  - id: l\n    run: { prompt: p }\n    outputs: { one: text }\n  - id: r\n    foreach: \"{{l.one}}\"\n    run: { prompt: p }", "stages[1].foreach", "{{l.one}} is not a list")]
	[InlineData("  - id: l\n    run: { prompt: p }\n    outputs: { items: list }\n  - id: r\n    foreach: \"x {{l.items}}\"\n    run: { prompt: p }", "stages[1].foreach", "foreach takes one reference to a list")]
	[InlineData("  - id: g\n    stages: [{ id: c, run: { prompt: p }, then: z }]\n  - id: z\n    run: { prompt: p }", "stages[0].stages[0].then", "\"z\" is not a stage beside \"c\"")]
	[InlineData("  - id: g\n    stages: [{ id: c, run: { prompt: p }, outputs: { n: text } }]\n  - id: z\n    run: { prompt: \"{{c.n}}\" }", "stages[1].run.prompt", "{{c.n}} is inside stage \"g\"; read an output of \"g\" instead")]
	[InlineData("  - id: l\n    run: { prompt: p }\n    outputs: { items: list }\n  - id: r\n    foreach: \"{{l.items}}\"\n    run: { prompt: p }\n  - id: z\n    run: { prompt: \"{{r.item}}\" }", "stages[2].run.prompt", "{{r.item}} is readable only inside repeated stage \"r\"")]
	[InlineData("  - id: l\n    run: { prompt: p }\n    outputs: { items: list }\n  - id: r\n    foreach: \"{{l.items}}\"\n    run: { prompt: p }\n    then: { ask: \"{{r.item}}?\", choices: { a: done, b: l } }", "stages[1].then.ask", "{{r.item}} is not set here")]
	[InlineData("  - id: l\n    run: { prompt: p }\n    outputs: { items: list }\n  - id: r\n    foreach: \"{{l.items}}\"\n    stages: [{ id: c, run: { prompt: p }, outputs: { n: text } }]\n    then: { ask: \"{{c.n}}?\", choices: { a: done, b: l } }", "stages[1].then.ask", "{{c.n}} is one entry's value")]
	[InlineData("  - id: g\n    agent: a\n    stages: [{ id: c, run: { prompt: p } }]", "stages[0].agent", "a stage with stages runs no turn of its own")]
	[InlineData("  - id: g\n    context: fork\n    stages: [{ id: c, agent: a, run: { prompt: p } }]", "stages[0].stages[0].agent", "stage \"c\" runs in the conversation of \"g\"")]
	[InlineData("  - id: g\n    stages: [{ id: c, context: fresh, run: { prompt: p } }]\n  - id: z\n    context: { reuse: c }\n    run: { prompt: p }", "stages[1].context.reuse", "\"c\" is not a stage beside \"z\"")]
	[InlineData("  - id: l\n    run: { prompt: p }\n    outputs: { items: list }\n  - id: r\n    foreach: \"{{l.items}}\"\n    context: fresh\n    run: { prompt: p }\n  - id: z\n    context: { reuse: r }\n    run: { prompt: p }", "stages[2].context.reuse", "opens a conversation per entry")]
	[InlineData("  - id: g\n    stages: [{ id: c, run: { prompt: p }, outputs: { n: text } }]\n    if: \"{{c.n}}?\"", "stages[0].if", "{{c.n}} is set inside stage \"g\"; only its outputs and then read it")]
	public void ReportsEachTreeRuleAtItsField(string stages, string path, string message) =>
		Assert.Contains(Errors(stages), d => d.Path == path && d.Message.Contains(message, StringComparison.Ordinal));

	[Fact]
	public void AReuseNamingNoSiblingIsReportedEvenWhereAnAgentAsksForItsConversation() {
		var errors = Errors("  - id: g\n    context: { reuse: nowhere }\n    stages: [{ id: c, agent: a, run: { prompt: p } }]");
		Assert.Contains(errors, d => d.Path == "stages[0].context.reuse" && d.Message.Contains("\"nowhere\" names no stage", StringComparison.Ordinal));
	}

	[Fact]
	public void ARepeatedOutputCopiedByAStageWithStagesIsAList() =>
		Assert.Empty(Errors("""
			  - id: l
			    run: { prompt: p }
			    outputs: { items: list }
			  - id: g
			    outputs: { notes: "{{r.note}}" }
			    stages:
			      - id: r
			        foreach: "{{l.items}}"
			        run: { prompt: p }
			        outputs: { note: text }
			  - id: each
			    foreach: "{{g.notes}}"
			    run: { prompt: "{{each.item}}" }
			"""));

	[Fact]
	public void ReaderAndTreeErrorsAreReportedTogether() {
		var errors = Errors("  - id: a\n    run: { prompt: p }\n    outputs: { n: number }\n    then: nowhere");
		Assert.Contains(errors, d => d.Path == "stages[0].outputs.n");
		Assert.Contains(errors, d => d.Path == "stages[0].then");
	}

	[Fact]
	public void AcceptsTheBriefsExampleAndReadsItsTree() {
		string text = """
			id: x
			description: d
			agents: { opus: { provider: p, model: opus }, fable: { provider: p, model: fable } }
			stages:
			  - id: design
			    run: { prompt: Design. }
			    outputs: { groups: list }
			  - id: group
			    foreach: "{{design.groups}}"
			    agent: opus
			    context: fork
			    outputs: { pr: "{{pr.pr}}", report: "{{report.report}}" }
			    stages:
			      - id: plan
			        run: { prompt: "Tasks of {{group.item}}." }
			        outputs: { tasks: list }
			      - id: task
			        foreach: "{{plan.tasks}}"
			        context: fork
			        run: { prompt: "Implement {{task.item}} in group {{group.index}}." }
			      - id: review
			        agent: fable
			        context: fresh
			        run: { prompt: "Review into review-{{group.index}}.md." }
			        outputs: { findings: files }
			        then: { ask: "Does {{review.findings}} list a bug?", choices: { clean: pr, fix: fix }, limit: 2 }
			      - id: fix
			        run: { prompt: "Fix {{review.findings}}." }
			        then: review
			      - id: pr
			        run: { prompt: Open the PR. }
			        outputs: { pr: pr }
			      - id: report
			        run: { prompt: "Report {{pr.pr}}." }
			        outputs: { report: files }
			  - id: consistency
			    run: { prompt: "Read {{group.report}}." }
			""";
		var read = WorkflowReader.Read(text, "x");
		Assert.DoesNotContain(read.Diagnostics, d => !d.IsWarning);
		var definition = read.Definition!;
		var group = definition.Stage("group");
		Assert.Equal(["design", "group", "plan", "task", "review", "fix", "pr", "report", "consistency"], definition.All.Select(s => s.Id));
		Assert.Equal([("pr", WorkflowValueType.Pr), ("report", WorkflowValueType.Files)], group.Outputs.Select(o => (o.Name, o.Type)));
		Assert.Equal(group, definition.Parent(definition.Stage("task")));
		Assert.Equal(group, definition.Opener(definition.Stage("fix")));
		Assert.Equal("opus", definition.AgentOf(definition.Stage("fix"), [])!.Name);
		Assert.Equal("fable", definition.AgentOf(definition.Stage("review"), [])!.Name);
		Assert.Null(definition.Opener(definition.Stage("consistency")));
	}
}
