using System.Text.Json;
using Weavie.Workflows;
using Xunit;

namespace Weavie.Workflows.Tests;

public sealed class WorkflowConfidenceReaderTests {
	private static string Document(string condition, string then) => $$"""
		id: check
		description: Check evidence.
		stages:
		  - id: work
		    if: {{condition}}
		    run: { prompt: Work. }
		    then: {{then}}
		""";

	[Theory]
	[InlineData("0", 0)]
	[InlineData("1", 1)]
	[InlineData("0.85", 0.85)]
	public void ReadsConfidenceAndMixedDescribedChoices(string confidence, double expected) {
		var read = WorkflowReader.ReadProposal(Document($"{{ ask: Ready?, confidence: {confidence} }}",
			$"{{ ask: Complete?, confidence: {confidence}, choices: {{ yes: {{ then: done, description: All checks passed. }}, unsure: work }} }}"));
		var stage = Assert.Single(read.Definition!.Stages);
		Assert.Equal(expected, stage.If!.Confidence);
		var decision = Assert.IsType<WorkflowThen.Decide>(stage.Then);
		Assert.Equal(expected, decision.Question.Confidence);
		Assert.Equal([new WorkflowChoice("yes", "done", "All checks passed."), new WorkflowChoice("unsure", "work", null)], decision.Choices);
	}

	[Theory]
	[InlineData("-0.1")]
	[InlineData("1.1")]
	[InlineData("NaN")]
	[InlineData(".inf")]
	[InlineData("1e999")]
	[InlineData("true")]
	[InlineData("nope")]
	public void InvalidThresholdsArePositionedErrors(string value) {
		var read = WorkflowReader.ReadProposal(Document($"{{ ask: Ready?, confidence: {value} }}",
			$"{{ ask: Done?, confidence: {value}, choices: {{ yes: done, no: work }} }}"));
		Assert.Null(read.Definition);
		Assert.Contains(read.Diagnostics, d => d.Path == "stages[0].if.confidence" && !d.IsWarning);
		Assert.Contains(read.Diagnostics, d => d.Path == "stages[0].then.confidence" && !d.IsWarning);
	}

	[Fact]
	public void UserDecisionsCannotDeclareConfidence() {
		var read = WorkflowReader.ReadProposal(Document("{ ask: Ready?, by: user, confidence: 0.8 }",
			"{ ask: Done?, by: user, confidence: 0.8, choices: { yes: done, no: work } }"));
		Assert.Null(read.Definition);
		Assert.Contains(read.Diagnostics, d => d.Path == "stages[0].if.confidence");
		Assert.Contains(read.Diagnostics, d => d.Path == "stages[0].then.confidence");
	}

	[Theory]
	[InlineData("{ description: Missing target. }")]
	[InlineData("{ then: done, description: [not, text] }")]
	[InlineData("{ then: done, unknown: invalid }")]
	[InlineData("{ then: missing }")]
	public void DescribedChoicesStillValidateTheirShapeAndTarget(string choice) {
		var read = WorkflowReader.ReadProposal(Document("Ready?", $"{{ ask: Done?, choices: {{ yes: {choice}, no: work }} }}"));
		Assert.Null(read.Definition);
		Assert.Contains(read.Diagnostics, d => !d.IsWarning && d.Path.StartsWith("stages[0].then.choices.yes", StringComparison.Ordinal));
	}

	[Fact]
	public void ShorthandUsesNoOverrideAndPublishedSchemaHasNumericBounds() {
		var read = WorkflowReader.ReadProposal(Document("Ready?", "done"));
		Assert.Null(read.Definition!.Stages[0].If!.Confidence);
		using var schema = JsonDocument.Parse(WorkflowSchema.Json);
		var confidence = schema.RootElement.GetProperty("properties").GetProperty("stages").GetProperty("items")
			.GetProperty("properties").GetProperty("if").GetProperty("oneOf")[1].GetProperty("properties").GetProperty("confidence");
		Assert.Equal("number", confidence.GetProperty("type").GetString());
		Assert.Equal(0, confidence.GetProperty("minimum").GetDouble());
		Assert.Equal(1, confidence.GetProperty("maximum").GetDouble());
	}
}
