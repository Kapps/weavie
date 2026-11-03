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

	private WorkflowCatalog Catalog() => new(Worktree, Personal);

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
		Assert.Equal("quick-fix workflow", Catalog().Resolve("quick-fix").Description);
	}

	[Fact]
	public void BrokenFilesAreListedWithTheirErrorsAndRefuseToResolve() {
		_dir.WriteFile("personal/broken.yaml", "id: broken\ndescription: d\nstages: nope\n");
		var entry = Catalog().List().Single(e => e.Id == "broken");
		Assert.Null(entry.Definition);
		Assert.Contains(entry.Diagnostics, d => d.Path == "stages");
		var ex = Assert.Throws<WorkflowException>(() => Catalog().Resolve("broken"));
		Assert.Contains("invalid", ex.Message, StringComparison.Ordinal);
		Assert.Throws<WorkflowException>(() => Catalog().Resolve("missing"));
	}

	[Fact]
	public void EditsShowWithoutAnyCaching() {
		var catalog = Catalog();
		Assert.DoesNotContain(catalog.List(), e => e.Id == "fresh");
		_dir.WriteFile("worktree/.weavie/workflows/fresh.yaml", Minimal("fresh"));
		Assert.Contains(catalog.List(), e => e.Id == "fresh");
	}

	[Fact]
	public void ValidateResolvesRelativePathsAgainstTheWorktree() {
		_dir.WriteFile("worktree/.weavie/workflows/ok.yaml", Minimal("ok"));
		_dir.WriteFile("worktree/.weavie/workflows/bad.yaml", Minimal("other"));
		Assert.Empty(Catalog().Validate(".weavie/workflows/ok.yaml"));
		Assert.Contains(Catalog().Validate(_dir.Combine("worktree", ".weavie", "workflows", "bad.yaml")), d => d.Path == "id");
		Assert.Throws<WorkflowException>(() => Catalog().Validate("missing.yaml"));
		Assert.Throws<WorkflowException>(() => Catalog().Validate(".weavie/workflows/ok.yml"));
	}

	[Fact]
	public void SchemaDescribesTheFieldTable() {
		var schema = JsonNode.Parse(WorkflowSchema.Json)!.AsObject();
		Assert.Equal("Weavie workflow", schema["title"]!.GetValue<string>());
		Assert.False(schema["additionalProperties"]!.GetValue<bool>());
		Assert.Equal(["id", "description", "stages"], schema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
		var stage = schema["properties"]!["stages"]!["items"]!["properties"]!.AsObject();
		Assert.Equal(["id", "if", "run", "then"], stage.Select(p => p.Key));
		Assert.Equal(["prompt", "skill", "args", "outputs"], stage["run"]!["properties"]!.AsObject().Select(p => p.Key));
		Assert.Equal(["ask", "choices", "limit", "by"], stage["then"]!["oneOf"]![1]!["properties"]!.AsObject().Select(p => p.Key));
	}
}
