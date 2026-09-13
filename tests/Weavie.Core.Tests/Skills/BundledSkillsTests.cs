using System.Text.Json;
using Weavie.Core.Skills;
using Xunit;

namespace Weavie.Core.Tests.Skills;

public sealed class BundledSkillsTests {
	[Fact]
	public void ShippedCatalogPointsToPackagedSkillsAndTheirPrivacyReference() {
		string catalog = BundledSkills.Catalog();
		foreach (string name in new[] { "report-weavie-bug", "request-weavie-feature" }) {
			string path = Path.Combine(AppContext.BaseDirectory, "skills", name, "SKILL.md");
			Assert.Contains(path, catalog, StringComparison.Ordinal);
			string skill = File.ReadAllText(path);
			Assert.Contains("../references/issue-reporting.md", skill, StringComparison.Ordinal);
			Assert.DoesNotContain("Before gathering evidence", catalog, StringComparison.Ordinal);
		}
		string policy = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "skills", "references", "issue-reporting.md"));
		Assert.Contains("Never include source code from non-public repositories", policy, StringComparison.Ordinal);
	}

	[Fact]
	public void CatalogReadsMetadataOnlyFromTheSuppliedBundle() {
		using var directory = new TempDirectory("weavie-bundled-skills");
		string root = directory.Combine("bundle");
		string skillDirectory = Path.Combine(root, "example");
		Directory.CreateDirectory(skillDirectory);
		string path = Path.Combine(skillDirectory, "SKILL.md");
		File.WriteAllText(path, "---\n" + JsonSerializer.Serialize(new {
			name = "example",
			description = "The maintained description.",
		}) + "\n---\nOnly load this body on demand.\n");
		string global = directory.Combine(".agents", "skills", "unrelated");
		Directory.CreateDirectory(global);
		File.WriteAllText(Path.Combine(global, "SKILL.md"), "not a bundled skill");

		string catalog = BundledSkills.Catalog(root);

		Assert.Contains("example: The maintained description.", catalog, StringComparison.Ordinal);
		Assert.Contains(path, catalog, StringComparison.Ordinal);
		Assert.DoesNotContain("Only load this body", catalog, StringComparison.Ordinal);
		Assert.DoesNotContain("unrelated", catalog, StringComparison.Ordinal);
	}

	[Fact]
	public void MissingBundleFailsInsteadOfSilentlyOmittingSkills() {
		using var directory = new TempDirectory("weavie-missing-skills");
		Assert.Throws<InvalidDataException>(() => BundledSkills.Catalog(directory.Path));
		Assert.Throws<DirectoryNotFoundException>(() => BundledSkills.Catalog(directory.Combine("missing")));
	}
}
