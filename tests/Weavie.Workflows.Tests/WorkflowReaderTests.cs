using Weavie.Workflows;
using Xunit;

namespace Weavie.Workflows.Tests;

public sealed class WorkflowReaderTests {
	private const string Valid = """
		id: ship
		description: Ship it.
		inputs:
		  task: text
		  spec: url?
		stages:
		  - id: spec
		    if: "{{inputs.task}} is complex"
		    run:
		      skill: spec
		      args: "{{inputs.task}}"
		      outputs: { notionUrl: url, tasks: list }
		    then:
		      ask: Approve the spec?
		      by: user
		      choices: { approve: build, revise: spec }
		  - id: build
		    if: { ask: "Build {{inputs.task}} now?", by: user }
		    run: { prompt: "Build {{inputs.task}} per {{spec.notionUrl}}" }
		    then:
		      ask: "Done with {{spec.tasks}}?"
		      choices: { yes: done, no: build }
		      limit: 3
		""";

	[Fact]
	public void AProposalIsReadUnderItsOwnId() {
		var result = WorkflowReader.ReadProposal(Valid);
		Assert.Equal("ship", result.Definition!.Id);
		Assert.Empty(result.Diagnostics);
	}

	[Theory]
	[InlineData("description: d\nstages:\n  - id: a\n    run: { prompt: go }\n", "missing required key \"id\"")]
	[InlineData("id: Not Valid\ndescription: d\nstages:\n  - id: a\n    run: { prompt: go }\n", "Not Valid")]
	public void AProposalReportsAMissingOrMalformedIdAsAnOrdinaryError(string text, string message) {
		var result = WorkflowReader.ReadProposal(text);
		Assert.Null(result.Definition);
		Assert.Contains(result.Diagnostics, d => d.ToString().Contains(message, StringComparison.Ordinal));
	}

	[Fact]
	public void AProposalThatIsNotYamlReportsWhereItBroke() {
		var diagnostic = Assert.Single(WorkflowReader.ReadProposal("id: a\ndescription: [\n").Diagnostics);
		Assert.Equal(3, diagnostic.Position.Line);
	}

	[Fact]
	public void ReadsAValidWorkflowIntoTypedRecords() {
		var result = WorkflowReader.Read(Valid, "ship");
		Assert.Empty(result.Diagnostics);
		var definition = result.Definition!;
		Assert.Equal("ship", definition.Id);
		Assert.Equal(
			[("task", WorkflowValueType.Text, false), ("spec", WorkflowValueType.Url, true)],
			definition.Inputs.Select(i => (i.Name, i.Type, i.Optional)));
		var spec = definition.Stages[0];
		Assert.Equal("spec", spec.Run.Skill);
		Assert.Equal(("{{inputs.task}} is complex", WorkflowAnswerer.Model), (spec.If!.Ask.Source, spec.If.By));
		Assert.Equal(["notionUrl", "tasks"], spec.Run.Outputs.Select(o => o.Name));
		var approval = Assert.IsType<WorkflowThen.Decide>(spec.Then);
		Assert.Equal((WorkflowAnswerer.User, null), (approval.Question.By, approval.Limit));
		var build = definition.Stages[1];
		Assert.Equal(WorkflowAnswerer.User, build.If!.By);
		var decide = Assert.IsType<WorkflowThen.Decide>(build.Then);
		Assert.Equal(3, decide.Limit);
		Assert.Equal([new WorkflowChoice("yes", "done", null), new WorkflowChoice("no", "build", null)], decide.Choices);
		Assert.Equal(Valid, definition.SourceText);
	}

	[Theory]
	[InlineData("id: x\ndescription: d\nruns-on: linux\nstages: [{ id: a, run: { prompt: p } }]", "unknown key \"runs-on\"", "runs-on", 3)]
	[InlineData("id: x\ndescription: d\nstages: [{ id: a }]", "missing required key \"run\"", "stages[0]", 3)]
	[InlineData("id: x\ndescription: d\nstages: []", "expected a non-empty list", "stages", 3)]
	[InlineData("id: x\ndescription: d\nstages: [{ id: A, run: { prompt: p } }]", "lowercase words", "stages[0].id", 3)]
	[InlineData("id: x\ndescription: d\nstages: [{ id: a, run: { prompt: p }, then: { ask: q, by: robot, choices: { y: done, n: a } } }]", "expected one of model, user", "stages[0].then.by", 3)]
	[InlineData("id: x\ndescription: d\nstages: [{ id: a, run: { prompt: p }, then: { ask: q, by: user, choices: { y: done, n: a }, limit: 0 } }]", "at least 1", "stages[0].then.limit", 3)]
	[InlineData("id: x\ndescription: d\nstages: [{ id: a, if: { ask: q, by: user, choices: { y: a } }, run: { prompt: p } }]", "unknown key \"choices\"", "stages[0].if.choices", 3)]
	[InlineData("id: x\ndescription: d\ninputs: { task: number }\nstages: [{ id: a, run: { prompt: p } }]", "expected one of text", "inputs.task", 3)]
	public void ShapeErrorsNameThePathAndLine(string text, string message, string path, int line) {
		var diagnostic = Assert.Single(WorkflowReader.Read(text, "x").Diagnostics);
		Assert.Contains(message, diagnostic.Message, StringComparison.Ordinal);
		Assert.Equal(path, diagnostic.Path);
		Assert.Equal(line, diagnostic.Position.Line);
		Assert.Contains($"at {path} (line {line}", diagnostic.ToString(), StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("stages: [{ id: a, run: { prompt: p } }]", "must match the file name")]
	[InlineData("stages: [{ id: a, run: { prompt: p } }, { id: a, run: { prompt: p } }]", "used twice")]
	[InlineData("stages: [{ id: a, run: { prompt: p, skill: s } }]", "exactly one of prompt or skill")]
	[InlineData("stages: [{ id: a, run: { } }]", "exactly one of prompt or skill")]
	[InlineData("stages: [{ id: a, run: { prompt: p, args: x } }]", "args applies only to a skill")]
	[InlineData("stages: [{ id: a, else: done, run: { prompt: p } }]", "unknown key \"else\"")]
	[InlineData("stages: [{ id: a, run: { prompt: p }, then: b }]", "\"b\" is neither a stage id nor done")]
	[InlineData("stages: [{ id: a, run: { prompt: p }, then: { ask: q, by: user, choices: { yes: done } } }]", "at least two choices")]
	[InlineData("stages: [{ id: a, run: { prompt: p }, then: { ask: q, by: user, choices: { yes: done, no: a }, limit: 2 } }]", "limit applies only to decisions the model answers")]
	[InlineData("stages: [{ id: a, run: { prompt: \"{{inputs.missing}}\" } }]", "names no declared input")]
	[InlineData("stages: [{ id: a, run: { prompt: \"{{a.out}}\" } }]", "names no output declared")]
	[InlineData("stages: [{ id: a, run: { prompt: \"{{ oops }}\" } }]", "must be {{run.documents}}")]
	[InlineData("stages: [{ id: a, run: { prompt: \"{{inputs.x\" } }]", "no closing")]
	public void SemanticErrorsAreReported(string stages, string message) {
		string fileId = message.Contains("file name", StringComparison.Ordinal) ? "other" : "x";
		var result = WorkflowReader.Read($"id: x\ndescription: d\n{stages}", fileId);
		Assert.Null(result.Definition);
		Assert.Contains(result.Diagnostics, d => d.Message.Contains(message, StringComparison.Ordinal));
	}

	[Fact]
	public void AModelQuestionReferencingNoValuesIsValidButWarned() {
		var result = WorkflowReader.Read(
			"id: x\ndescription: d\nstages: [{ id: a, if: \"is it big?\", run: { prompt: p }, then: { ask: \"done?\", choices: { yes: done, no: a } } }]", "x");
		Assert.NotNull(result.Definition);
		Assert.Equal(["stages[0].if", "stages[0].then.ask"], result.Diagnostics.Select(d => d.Path));
		Assert.All(result.Diagnostics, d => Assert.True(d.IsWarning));
		Assert.StartsWith("warning: the model answering this has no tools", result.Diagnostics[0].ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public void AThenDecisionAfterAStageWithOutputsIsNotWarned() {
		var result = WorkflowReader.Read(
			"id: x\ndescription: d\nstages: [{ id: a, run: { prompt: p, outputs: { left: list } }, then: { ask: \"done?\", choices: { yes: done, no: a } } }]", "x");
		Assert.NotNull(result.Definition);
		Assert.Empty(result.Diagnostics);
	}

	[Fact]
	public void YamlSyntaxErrorsBecomeOneDiagnostic() {
		var diagnostic = Assert.Single(WorkflowReader.Read("id: [x\n", "x").Diagnostics);
		Assert.Equal("", diagnostic.Path);
	}
}
