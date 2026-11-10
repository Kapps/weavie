using System.Text.Json;
using Xunit;

namespace Weavie.Workflows.Tests;

public sealed class WorkflowDocumentsTests {
	private const string Flow = """
		id: shared
		description: shared documents
		inputs: { docs: files? }
		stages:
		  - id: draft
		    run: { prompt: "Write {{run.documents}}/design.md", outputs: { docs: files } }
		    then:
		      ask: "Approve {{draft.docs}}?"
		      choices: { yes: done, no: draft }
		""";

	[Fact]
	public void SharedFilesRenderValidatePersistAndBecomeExplicitDecisionEvidence() {
		using var dir = new TempDirectory("workflow-documents");
		string worktree = dir.Combine("worktree");
		string documents = dir.Combine("documents");
		dir.WriteFile("worktree/.weavie/workflows/shared.yaml", Flow);
		dir.WriteFile("documents/design.md", "The design");
		string path = Path.Combine(documents, "design.md");
		var catalog = TestWorkflows.Catalog(worktree, dir.Combine("personal"));
		var inputs = JsonSerializer.SerializeToElement(new { docs = new[] { path } });
		Assert.Throws<WorkflowException>(() => catalog.Prepare("shared", inputs));
		var launch = catalog.Prepare("shared", inputs, documents);
		var machine = new WorkflowTransitions(launch.Definition, 3, true, 0.8, worktree, TestWorkflows.Words);
		var start = machine.Start("r-docs", launch.SourcePath, launch.Inputs, documents, null);
		Assert.Equal($"Write {documents}/design.md", start.Run.Kickoff!.Text);
		var restored = WorkflowRunDocument.Read(WorkflowRunDocument.Write(start.Run))!;
		Assert.Equal(documents, restored.DocumentsDirectory);
		var completion = machine.Complete(restored, inputs, "written");
		Assert.Empty(completion.Errors);
		var plan = Assert.IsType<WorkflowEffect.Decide>(Assert.Single(completion.Step.Effects)).Plan;
		Assert.Equal(documents, plan.DocumentsDirectory);
		Assert.Equal(path, Assert.Single(plan.FilePaths));
	}

	[Fact]
	public void SharedFileAccessIsLimitedToTheGrantedDirectory() {
		using var dir = new TempDirectory("workflow-documents-paths");
		string worktree = dir.Combine("worktree");
		string documents = dir.Combine("documents");
		dir.WriteFile("documents/design.md", "The design");
		dir.WriteFile("documents-other/private.md", "Unrelated run");
		foreach (string path in new[] { documents, dir.Combine("documents-other/private.md"), Path.Combine(documents, "../documents-other/private.md"), "../documents/design.md" }) {
			Assert.False(WorkflowFilePaths.TryResolve(worktree, documents, path, out _, out _));
		}
		Assert.True(WorkflowFilePaths.TryResolve(worktree, documents, Path.Combine(documents, "nested/../design.md"), out string label, out string full));
		Assert.Equal(Path.Combine(documents, "design.md"), full);
		Assert.Equal(full, label);
	}

	[Theory]
	[InlineData("{}")]
	[InlineData("null")]
	public void IncompletePersistedEnvelopeIsRejected(string text) =>
		Assert.Throws<JsonException>(() => WorkflowRunDocument.Read(text));

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("relative/path")]
	public void InvalidPersistedDocumentsOwnershipIsRejected(string? documents) {
		var definition = WorkflowReader.Read(Flow, "shared").Definition!;
		var machine = new WorkflowTransitions(definition, 3, true, 0.8, Path.GetTempPath(), TestWorkflows.Words);
		var run = machine.Start("r-invalid", "source", [], Path.GetTempPath(), null).Run with { DocumentsDirectory = documents! };
		Assert.Throws<JsonException>(() => WorkflowRunDocument.Read(WorkflowRunDocument.Write(run)));
	}

	[Theory]
	[InlineData("{{run.missing}}", "draft")]
	[InlineData("{{run.documents}}", "run")]
	public void UnknownRunReferencesAndReservedStageNamesAreRejected(string reference, string stage) {
		var result = WorkflowReader.Read($"id: shared\ndescription: d\nstages: [{{ id: {stage}, run: {{ prompt: '{reference}' }} }}]", "shared");
		Assert.Null(result.Definition);
		Assert.Contains(result.Diagnostics, d => !d.IsWarning);
	}
}
