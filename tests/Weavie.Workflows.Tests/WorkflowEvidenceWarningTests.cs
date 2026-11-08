using System.Text.Json;
using Xunit;

namespace Weavie.Workflows.Tests;

public sealed class WorkflowEvidenceWarningTests {
	private const string Flow = """
		id: evidence
		description: advisory evidence
		inputs: { directory: text?, docs: files? }
		stages:
		  - id: draft
		    run:
		      prompt: Write the evidence.
		      outputs: { document: files, video: files, note: text }
		    then:
		      ask: 'Review README.md?'
		      choices: { approve: done, revise: draft }
		""";

	private static WorkflowReadResult Read(string question) => WorkflowReader.Read(
		Flow.Replace("Review README.md?", question, StringComparison.Ordinal), "evidence");

	[Fact]
	public void BothFlagsArePositionedWarningsAndTheWorkflowRemainsRunnable() {
		using var dir = new TempDirectory("weavie-evidence-warnings");
		dir.WriteFile(".weavie/workflows/evidence.yaml", Flow);
		var catalog = TestWorkflows.Catalog(dir.Path, dir.Combine("personal"));
		var diagnostics = catalog.Validate("evidence");
		Assert.Equal(3, diagnostics.Count);
		Assert.All(diagnostics, diagnostic => {
			Assert.True(diagnostic.IsWarning);
			Assert.Equal("stages[0].then.ask", diagnostic.Path);
			Assert.Equal(10, diagnostic.Position.Line);
			Assert.Equal(7, diagnostic.Position.Column);
		});
		Assert.Contains(diagnostics, d => d.Message.Contains("path-like", StringComparison.Ordinal));
		Assert.Contains(diagnostics, d => d.Message.Contains("{{draft.document}}", StringComparison.Ordinal));
		Assert.Contains(diagnostics, d => d.Message.Contains("{{draft.video}}", StringComparison.Ordinal));
		var launch = catalog.Prepare("evidence", JsonDocument.Parse("{}").RootElement);
		var machine = new WorkflowTransitions(launch.Definition, 3, true, 0.8, dir.Path, TestWorkflows.Words);
		Assert.Equal(WorkflowPhase.Open, machine.Start("r", launch.SourcePath, launch.Inputs).Run.Phase);
	}

	[Theory]
	[InlineData("README.md")]
	[InlineData(".gitignore")]
	[InlineData("docs/review.md")]
	[InlineData("docs/review")]
	[InlineData("docs\\review.md")]
	[InlineData("/tmp/review.md")]
	[InlineData("C:\\work\\review.md")]
	[InlineData("{{inputs.directory}}/requirements.md")]
	[InlineData("{{inputs.directory}}/{{draft.note}}")]
	[InlineData("{{draft.document}}/requirements.md")]
	[InlineData("{{draft.document}}.md")]
	[InlineData("{{inputs.docs}} and README.md")]
	public void LiteralAndComposedPathsFlagMissingContentEvenBesideAFileReference(string question) {
		var read = Read(question);
		Assert.NotNull(read.Definition);
		Assert.Single(read.Diagnostics, d => d.Message.Contains("path-like", StringComparison.Ordinal));
		Assert.All(read.Diagnostics, d => Assert.True(d.IsWarning));
	}

	[Theory]
	[InlineData("Does every criterion pass?")]
	[InlineData("Does version 1.2 meet 0.8 confidence?")]
	[InlineData("Is the score above 1/2?")]
	[InlineData("Are {{inputs.directory}} and {{draft.note}} complete?")]
	[InlineData("Review {{inputs.docs}} and {{draft.document}}?")]
	[InlineData("Review https://example.com/docs/README.md?")]
	[InlineData("Review [spec](HTTPS://example.com/docs/a(b).md) and <http://example.com/x.md>?")]
	[InlineData("Review https://{{inputs.directory}}/README.md?")]
	public void ValuesUrlsAndOrdinaryCriteriaDoNotLookLikeLocalPaths(string question) =>
		Assert.DoesNotContain(Read(question).Diagnostics, d => d.Message.Contains("path-like", StringComparison.Ordinal));

	[Fact]
	public void EachCurrentFileOutputMustBeReferencedButOtherOutputsAndQuestionsAreExcluded() {
		Assert.Empty(Read("Review {{draft.document}} {{draft.video}}?").Diagnostics);
		var onlyVideo = Assert.Single(Read("Review {{draft.document}} twice {{draft.document}}?").Diagnostics);
		Assert.Contains("{{draft.video}}", onlyVideo.Message);
		var read = WorkflowReader.Read(Flow.Replace("    then:", "    if: 'Ready {{inputs.docs}}?'\n    then:", StringComparison.Ordinal)
			.Replace("Review README.md?", "Review {{draft.document}} {{draft.video}}?", StringComparison.Ordinal), "evidence");
		Assert.Empty(read.Diagnostics);
	}

	[Fact]
	public void UserQuestionsReceiveNeitherEvidenceFlag() {
		var read = WorkflowReader.Read(Flow.Replace("    then:", "    if: { ask: 'Read missing.md?', by: user }\n    then:\n      by: user", StringComparison.Ordinal), "evidence");
		Assert.NotNull(read.Definition);
		Assert.Empty(read.Diagnostics);
	}

	[Theory]
	[InlineData("    if: 'Read docs/review.md?'", "stages[0].if", 5)]
	[InlineData("    if:\n      ask: 'Read docs/review.md?'", "stages[0].if.ask", 6)]
	public void IfWarningsPointToTheQuestionInEitherForm(string condition, string path, int line) {
		var read = WorkflowReader.Read($"id: evidence\ndescription: d\nstages:\n  - id: draft\n{condition}\n    run: {{ prompt: p }}", "evidence");
		var warning = Assert.Single(read.Diagnostics, d => d.Message.Contains("path-like", StringComparison.Ordinal));
		Assert.Equal(path, warning.Path);
		Assert.Equal(line, warning.Position.Line);
	}

	[Fact]
	public void IntentionalPathLikeProseMayBeFlaggedWithoutInvalidatingIt() {
		var read = Read("Is every response yes/no?");
		Assert.NotNull(read.Definition);
		Assert.Contains(read.Diagnostics, d => d.Message.Contains("path-like", StringComparison.Ordinal));
		Assert.All(read.Diagnostics, d => Assert.True(d.IsWarning));
	}

	[Theory]
	[InlineData("choices:", "unknown:")]
	[InlineData("approve: done", "approve: missing-stage")]
	[InlineData("Review README.md?", "Review {{draft.missing}}?")]
	public void StructuralAndReferenceErrorsRemainBlocking(string before, string after) {
		var read = WorkflowReader.Read(Flow.Replace(before, after, StringComparison.Ordinal), "evidence");
		Assert.Null(read.Definition);
		Assert.Contains(read.Diagnostics, d => !d.IsWarning);
	}
}
