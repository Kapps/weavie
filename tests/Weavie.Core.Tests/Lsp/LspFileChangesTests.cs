using Weavie.Core.FileActivity;
using Weavie.Core.Lsp;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class LspFileChangesTests {
	[Fact]
	public void FromInvalidations_FiltersAndMapsExplicitly() {
		string source = Path.Combine(Path.GetTempPath(), "source.cs");
		string prose = Path.Combine(Path.GetTempPath(), "notes.md");

		var mapped = LspFileChanges.FromInvalidations([
			new FileInvalidation(prose, FileInvalidationKind.Changed),
			new FileInvalidation(source, FileInvalidationKind.Deleted),
		]);

		var change = Assert.Single(mapped);
		Assert.Equal(new Uri(source).AbsoluteUri, change.Uri);
		Assert.Equal(FileChangeKind.Deleted, change.Kind);
	}
}
