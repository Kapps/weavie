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
	public void SchemaPublishesTheThreeContextsAndReuse() {
		using var schema = JsonDocument.Parse(WorkflowSchema.Json);
		var context = schema.RootElement.GetProperty("properties").GetProperty("stages").GetProperty("items")
			.GetProperty("properties").GetProperty("run").GetProperty("properties").GetProperty("context").GetProperty("oneOf");
		Assert.Equal(["continue", "fork", "fresh"], context[0].GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
		Assert.Equal(["reuse"], context[1].GetProperty("required").EnumerateArray().Select(value => value.GetString()));
	}

	private static string Reusing(string opener, string reuser) => $$"""
		id: flow
		description: Reuse.
		agents: { reviewer: { provider: claude-acp } }
		stages:
		  - id: review
		    run: { prompt: Review., {{opener}} }
		  - id: main
		    run: { prompt: Fix. }
		  - id: recheck
		    run: { prompt: Recheck., {{reuser}} }
		""";

	[Theory]
	[InlineData("context: fresh", WorkflowContext.Fresh)]
	[InlineData("context: fork", WorkflowContext.Fork)]
	public void AReusingStageOpensAsTheStageItReuses(string opener, WorkflowContext expected) {
		var read = WorkflowReader.Read(Reusing($"{opener}, agent: reviewer", "context: { reuse: review }"), "flow");
		Assert.NotNull(read.Definition);
		var recheck = read.Definition.Stages[2];
		Assert.Equal("review", recheck.Run.Reuse);
		Assert.Equal(expected, recheck.Run.Context);
		Assert.Same(read.Definition.Stages[0], read.Definition.Opener(recheck));
		Assert.Same(read.Definition.Stages[0], read.Definition.Opener(read.Definition.Stages[0]));
		Assert.Null(read.Definition.Opener(read.Definition.Stages[1]));
		Assert.Equal("reviewer", read.Definition.AgentOf(recheck, [])!.Name);
	}

	[Theory]
	[InlineData("context: fresh", "context: { reuse: other }", "stages[2].run.context.reuse", "\"other\" names no stage")]
	[InlineData("context: fresh", "context: { reuse: recheck }", "stages[2].run.context.reuse", "can't reuse its own conversation")]
	[InlineData("context: continue", "context: { reuse: review }", "stages[2].run.context.reuse", "runs in the main conversation")]
	[InlineData("context: { reuse: main }", "context: { reuse: review }", "stages[2].run.context.reuse", "stage \"review\" reuses \"main\"; name \"main\"")]
	[InlineData("context: fresh", "context: { reuse: review }, agent: reviewer", "stages[2].run.agent", "runs on that conversation's agent; set it on \"review\"")]
	[InlineData("context: fresh", "context: { reuse: review }, role: builder", "stages[2].run.role", "runs on that conversation's agent")]
	public void RejectsAReuseThatCannotContinueAConversation(string opener, string reuser, string path, string message) {
		var read = WorkflowReader.Read(Reusing(opener, reuser), "flow");
		Assert.Null(read.Definition);
		Assert.Contains(read.Diagnostics, diagnostic => diagnostic.Path == path && diagnostic.Message.Contains(message, StringComparison.Ordinal));
	}
}
