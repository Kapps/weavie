using Weavie.Core.Workflows;
using Xunit;

namespace Weavie.Core.Tests.Workflows;

public sealed class WorkflowReaderTests {
	private const string Valid = """
		id: ship
		description: Ship it.
		inputs:
		  task: text
		  spec: url?
		stages:
		  - id: spec
		    when: "{{inputs.task}} is complex"
		    else: build
		    do:
		      skill: spec
		      args: "{{inputs.task}}"
		      outputs: { notionUrl: url, tasks: list }
		    gate: approve
		  - id: build
		    do: { prompt: "Build {{inputs.task}} per {{spec.notionUrl}}" }
		    next:
		      prompt: "Done with {{spec.tasks}}?"
		      choices: { yes: done, no: build }
		      max: 3
		""";

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
		Assert.Equal("spec", spec.Do.Skill);
		Assert.Equal("build", spec.Else);
		Assert.True(spec.Gate);
		Assert.Equal(["notionUrl", "tasks"], spec.Do.Outputs.Select(o => o.Name));
		Assert.IsType<WorkflowNext.Sequential>(spec.Next);
		var choose = Assert.IsType<WorkflowNext.Choose>(definition.Stages[1].Next);
		Assert.Equal(3, choose.Max);
		Assert.Equal([new WorkflowChoice("yes", "done"), new WorkflowChoice("no", "build")], choose.Choices);
		Assert.Equal(Valid, definition.SourceText);
	}

	[Theory]
	[InlineData("id: x\ndescription: d\nruns-on: linux\nstages: [{ id: a, do: { prompt: p } }]", "unknown key \"runs-on\"", "runs-on", 3)]
	[InlineData("id: x\ndescription: d\nstages: [{ id: a }]", "missing required key \"do\"", "stages[0]", 3)]
	[InlineData("id: x\ndescription: d\nstages: []", "expected a non-empty list", "stages", 3)]
	[InlineData("id: x\ndescription: d\nstages: [{ id: A, do: { prompt: p } }]", "lowercase words", "stages[0].id", 3)]
	[InlineData("id: x\ndescription: d\nstages: [{ id: a, gate: always, do: { prompt: p } }]", "expected one of approve", "stages[0].gate", 3)]
	[InlineData("id: x\ndescription: d\nstages: [{ id: a, do: { prompt: p }, next: { prompt: q, choices: { y: done, n: a }, max: 0 } }]", "at least 1", "stages[0].next.max", 3)]
	[InlineData("id: x\ndescription: d\ninputs: { task: number }\nstages: [{ id: a, do: { prompt: p } }]", "expected one of text", "inputs.task", 3)]
	public void ShapeErrorsNameThePathAndLine(string text, string message, string path, int line) {
		var diagnostic = Assert.Single(WorkflowReader.Read(text, "x").Diagnostics);
		Assert.Contains(message, diagnostic.Message, StringComparison.Ordinal);
		Assert.Equal(path, diagnostic.Path);
		Assert.Equal(line, diagnostic.Position.Line);
		Assert.Contains($"at {path} (line {line}", diagnostic.ToString(), StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("stages: [{ id: a, do: { prompt: p } }]", "must match the file name")]
	[InlineData("stages: [{ id: a, do: { prompt: p } }, { id: a, do: { prompt: p } }]", "used twice")]
	[InlineData("stages: [{ id: a, do: { prompt: p, skill: s } }]", "exactly one of prompt or skill")]
	[InlineData("stages: [{ id: a, do: { } }]", "exactly one of prompt or skill")]
	[InlineData("stages: [{ id: a, do: { prompt: p, args: x } }]", "args applies only to a skill")]
	[InlineData("stages: [{ id: a, else: done, do: { prompt: p } }]", "else applies only with when")]
	[InlineData("stages: [{ id: a, do: { prompt: p }, next: b }]", "\"b\" is neither a stage id nor done")]
	[InlineData("stages: [{ id: a, do: { prompt: p }, next: { prompt: q, choices: { yes: done } } }]", "at least two choices")]
	[InlineData("stages: [{ id: a, do: { prompt: p }, next: { prompt: q, choices: { yes: done, unsure: a } } }]", "reserved")]
	[InlineData("stages: [{ id: a, do: { prompt: \"{{inputs.missing}}\" } }]", "names no declared input")]
	[InlineData("stages: [{ id: a, do: { prompt: \"{{a.out}}\" } }]", "names no output declared")]
	[InlineData("stages: [{ id: a, do: { prompt: \"{{ oops }}\" } }]", "must be {{inputs.<name>}}")]
	[InlineData("stages: [{ id: a, do: { prompt: \"{{inputs.x\" } }]", "no closing")]
	public void SemanticErrorsAreReported(string stages, string message) {
		string fileId = message.Contains("file name", StringComparison.Ordinal) ? "other" : "x";
		var result = WorkflowReader.Read($"id: x\ndescription: d\n{stages}", fileId);
		Assert.Null(result.Definition);
		Assert.Contains(result.Diagnostics, d => d.Message.Contains(message, StringComparison.Ordinal));
	}

	[Fact]
	public void YamlSyntaxErrorsBecomeOneDiagnostic() {
		var diagnostic = Assert.Single(WorkflowReader.Read("id: [x\n", "x").Diagnostics);
		Assert.Equal("", diagnostic.Path);
	}
}
