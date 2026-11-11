using System.Text.Json.Nodes;
using Weavie.TestSupport;
using Weavie.Workflows;
using Xunit;

namespace Weavie.Workflows.Tests;

public sealed class WorkflowCatalogTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-workflow-catalog");

	public void Dispose() => _dir.Dispose();

	private string Worktree => _dir.Combine("worktree");

	private string Personal => _dir.Combine("personal");

	private static System.Text.Json.JsonElement NoInputs => System.Text.Json.JsonDocument.Parse("{}").RootElement;

	private WorkflowCatalog Catalog() => TestWorkflows.Catalog(Worktree, Personal);

	private static string Minimal(string id) => $"id: {id}\ndescription: {id} workflow\nstages:\n  - id: a\n    run: {{ prompt: go }}\n";

	[Fact]
	public void EveryBuiltInIsValid() {
		var builtIns = Catalog().List().Where(e => e.Scope == WorkflowScope.BuiltIn).ToList();
		Assert.Equal(["quick-fix", "spec-driven"], builtIns.Select(e => e.Id));
		Assert.All(builtIns, entry => Assert.True(entry.Definition is not null, string.Join("\n", entry.Diagnostics)));
		Assert.All(builtIns, entry => Assert.Equal($"built-in:{entry.Id}.yaml", entry.Path));
	}

	[Fact]
	public void RepositoryOverridesPersonalOverridesBuiltInAndShadowedEntriesSayBy() {
		_dir.WriteFile("personal/quick-fix.yaml", Minimal("quick-fix"));
		_dir.WriteFile("worktree/.weavie/workflows/quick-fix.yaml", Minimal("quick-fix"));
		var entries = Catalog().List().Where(e => e.Id == "quick-fix").ToList();
		Assert.Equal(
			[(WorkflowScope.Repository, (WorkflowScope?)null), (WorkflowScope.Personal, WorkflowScope.Repository), (WorkflowScope.BuiltIn, WorkflowScope.Repository)],
			entries.Select(e => (e.Scope, e.ShadowedBy)));
		Assert.Equal("quick-fix workflow", entries.Single(e => e.ShadowedBy is null).Definition!.Description);
	}

	[Fact]
	public void BrokenFilesAreListedWithTheirErrorsAndRefuseToResolve() {
		_dir.WriteFile("personal/broken.yaml", "id: broken\ndescription: d\nstages: nope\n");
		var entry = Catalog().List().Single(e => e.Id == "broken");
		Assert.Null(entry.Definition);
		Assert.Contains(entry.Diagnostics, d => d.Path == "stages");
		var ex = Assert.Throws<WorkflowException>(() => Catalog().Prepare("broken", NoInputs));
		Assert.Contains("invalid", ex.Message, StringComparison.Ordinal);
		Assert.Throws<WorkflowException>(() => Catalog().Prepare("missing", NoInputs));
	}

	[Fact]
	public void EditsShowWithoutAnyCaching() {
		var catalog = Catalog();
		Assert.DoesNotContain(catalog.List(), e => e.Id == "fresh");
		_dir.WriteFile("worktree/.weavie/workflows/fresh.yaml", Minimal("fresh"));
		Assert.Contains(catalog.List(), e => e.Id == "fresh");
	}

	[Fact]
	public void ValidateChecksTheWinnerByIdEvenWhenItIsBroken() {
		_dir.WriteFile("personal/ok.yaml", "id: ok\ndescription: d\nstages: nope\n");
		_dir.WriteFile("worktree/.weavie/workflows/ok.yaml", Minimal("ok"));
		_dir.WriteFile("worktree/.weavie/workflows/bad.yaml", Minimal("other"));
		Assert.Empty(Catalog().Validate("ok"));
		Assert.Contains(Catalog().Validate("bad"), d => d.Path == "id");
		Assert.Empty(Catalog().Validate("quick-fix"));
	}

	[Fact]
	public void ValidateNamesTheWorkflowsWhenTheIdIsUnknown() {
		var ex = Assert.Throws<WorkflowException>(() => Catalog().Validate("missing"));
		Assert.Contains("quick-fix", ex.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void ValidateTextChecksAProposalWithoutTouchingAnyFile() {
		_dir.WriteFile("personal/ok.yaml", Minimal("ok"));
		string[] before = Directory.GetFiles(_dir.Path, "*", SearchOption.AllDirectories);
		Assert.Empty(Catalog().ValidateText(Minimal("ok")));
		Assert.Contains(Catalog().ValidateText("id: ok\ndescription: d\nstages: nope\n"), d => d.Path == "stages");
		Assert.Equal(before, Directory.GetFiles(_dir.Path, "*", SearchOption.AllDirectories));
		Assert.Equal(Minimal("ok"), File.ReadAllText(_dir.Combine("personal", "ok.yaml")));
	}

	[Fact]
	public void ValidateTextReportsWarnings() {
		string text = "id: w\ndescription: d\nstages:\n  - id: a\n    run: { prompt: go }\n    if: is it ready?\n";
		Assert.Contains(Catalog().ValidateText(text), d => d.IsWarning);
	}

	[Fact]
	public void ForkCopiesABuiltInIntoANewFolderAndWinsExactlyAsWritten() {
		string builtIn = Catalog().List().Single(e => e.Id == "quick-fix").Definition!.SourceText;
		string path = Catalog().Fork("quick-fix", WorkflowScope.Personal);
		Assert.Equal(_dir.Combine("personal", "quick-fix.yaml"), path);
		Assert.Equal(builtIn, File.ReadAllText(path));
		var entries = Catalog().List().Where(e => e.Id == "quick-fix").ToList();
		Assert.Equal(
			[(WorkflowScope.Personal, (WorkflowScope?)null), (WorkflowScope.BuiltIn, WorkflowScope.Personal)],
			entries.Select(e => (e.Scope, e.ShadowedBy)));
	}

	[Fact]
	public void ForkCopiesIntoTheRepositoryFromEitherLessSpecificScope() {
		string comment = "# keep me\n" + Minimal("mine");
		_dir.WriteFile("personal/mine.yaml", comment);
		Assert.Equal(comment, File.ReadAllText(Catalog().Fork("mine", WorkflowScope.Repository)));
		Assert.Equal(_dir.Combine("worktree", ".weavie", "workflows", "spec-driven.yaml"), Catalog().Fork("spec-driven", WorkflowScope.Repository));
	}

	[Fact]
	public void ForkRefusesABrokenWorkflowAndListsItsErrors() {
		_dir.WriteFile("personal/quick-fix.yaml", "id: quick-fix\ndescription: d\nstages: nope\n");
		var ex = Assert.Throws<WorkflowException>(() => Catalog().Fork("quick-fix", WorkflowScope.Repository));
		Assert.Contains("stages", ex.Message, StringComparison.Ordinal);
		Assert.False(Directory.Exists(_dir.Combine("worktree", ".weavie", "workflows")));
	}

	[Theory]
	[InlineData(WorkflowScope.Repository)]
	[InlineData(WorkflowScope.Personal)]
	[InlineData(WorkflowScope.BuiltIn)]
	public void ForkRefusesAnythingButAMoreSpecificScope(WorkflowScope target) {
		_dir.WriteFile("worktree/.weavie/workflows/mine.yaml", Minimal("mine"));
		var ex = Assert.Throws<WorkflowException>(() => Catalog().Fork("mine", target));
		Assert.Contains("mine.yaml", ex.Message, StringComparison.Ordinal);
		Assert.False(File.Exists(_dir.Combine("personal", "mine.yaml")));
		Assert.Equal(Minimal("mine"), File.ReadAllText(_dir.Combine("worktree", ".weavie", "workflows", "mine.yaml")));
	}

	[Fact]
	public void ForkNamesTheWorkflowsWhenTheIdIsUnknown() {
		var ex = Assert.Throws<WorkflowException>(() => Catalog().Fork("missing", WorkflowScope.Personal));
		Assert.Contains("quick-fix", ex.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void ForkFailsRatherThanReplaceWhatTheCatalogDoesNotList() {
		string target = _dir.Combine("personal", "quick-fix.yaml");
		Directory.CreateDirectory(target);
		var ex = Assert.ThrowsAny<Exception>(() => Catalog().Fork("quick-fix", WorkflowScope.Personal));
		Assert.True(ex is IOException or UnauthorizedAccessException, ex.ToString());
		Assert.True(Directory.Exists(target));
	}

	[Fact]
	public void FolderIsWhereTheCatalogReadsEvenBeforeItExists() {
		Assert.Equal(Personal, Catalog().Folder(WorkflowScope.Personal));
		Assert.Equal(_dir.Combine("worktree", ".weavie", "workflows"), Catalog().Folder(WorkflowScope.Repository));
		Assert.Throws<WorkflowException>(() => Catalog().Folder(WorkflowScope.BuiltIn));
	}

	[Fact]
	public void SchemaDescribesTheFieldTable() {
		var schema = JsonNode.Parse(WorkflowSchema.Json)!.AsObject();
		Assert.Equal("Workflow", schema["title"]!.GetValue<string>());
		Assert.Contains("authors may ignore intentional warnings", schema["description"]!.GetValue<string>(), StringComparison.Ordinal);
		Assert.False(schema["additionalProperties"]!.GetValue<bool>());
		Assert.Equal(["id", "description", "stages"], schema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
		var stage = schema["properties"]!["stages"]!["items"]!["properties"]!.AsObject();
		Assert.Equal(["id", "if", "run", "then"], stage.Select(p => p.Key));
		Assert.Equal(["context", "prompt", "skill", "args", "outputs", "agent"], stage["run"]!["properties"]!.AsObject().Select(p => p.Key));
		var agent = schema["properties"]!["agents"]!["additionalProperties"]!;
		Assert.Equal(["provider", "model", "options"], agent["properties"]!.AsObject().Select(p => p.Key));
		Assert.Equal(["provider"], agent["required"]!.AsArray().Select(n => n!.GetValue<string>()));
		Assert.Equal(["ask", "choices", "limit", "by", "confidence"], stage["then"]!["oneOf"]![1]!["properties"]!.AsObject().Select(p => p.Key));
		string ask = stage["then"]!["oneOf"]![1]!["properties"]!["ask"]!["description"]!.GetValue<string>();
		Assert.Contains("observable criteria, one concern per question", ask, StringComparison.Ordinal);
		Assert.Contains("current text of explicitly referenced files", ask, StringComparison.Ordinal);
		Assert.Equal(ask, stage["if"]!["oneOf"]![0]!["description"]!.GetValue<string>());
	}
}
