using System.Text.Json;
using Xunit;

namespace Weavie.Workflows.Tests;

public sealed class WorkflowContextReaderTests {
	private static string Document(string context) => $$"""
		id: flow
		description: Conversation context.
		stages:
		  - id: work
		    run:
		      prompt: Work.
		      {{context}}
		""";

	[Theory]
	[InlineData("", WorkflowContext.Continue)]
	[InlineData("context: continue", WorkflowContext.Continue)]
	[InlineData("context: fork", WorkflowContext.Fork)]
	[InlineData("context: fresh", WorkflowContext.Fresh)]
	public void ReadsConversationContext(string context, WorkflowContext expected) {
		var read = WorkflowReader.Read(Document(context), "flow");
		Assert.NotNull(read.Definition);
		Assert.Equal(expected, Assert.Single(read.Definition.Stages).Run.Context);
	}

	[Theory]
	[InlineData("other")]
	[InlineData("Fresh")]
	[InlineData("[]")]
	[InlineData("true")]
	public void RejectsInvalidContextAtItsField(string context) {
		var read = WorkflowReader.Read(Document($"context: {context}"), "flow");
		Assert.Null(read.Definition);
		Assert.Contains(read.Diagnostics, diagnostic => diagnostic.Path == "stages[0].run.context" && !diagnostic.IsWarning);
	}

	[Fact]
	public void SchemaPublishesTheSameThreeContexts() {
		using var schema = JsonDocument.Parse(WorkflowSchema.Json);
		var context = schema.RootElement.GetProperty("properties").GetProperty("stages").GetProperty("items")
			.GetProperty("properties").GetProperty("run").GetProperty("properties").GetProperty("context");
		Assert.Equal(["continue", "fork", "fresh"], context.GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
	}
}
