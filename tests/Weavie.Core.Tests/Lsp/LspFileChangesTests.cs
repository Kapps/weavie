using Weavie.Core.FileActivity;
using Weavie.Core.Lsp;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class LspFileChangesTests {
	[Fact]
	public void FromInvalidations_FiltersAndMapsExplicitly() {
		string source = Path.Combine(Path.GetTempPath(), "source.cs");
		string python = Path.Combine(Path.GetTempPath(), "source.py");
		string rust = Path.Combine(Path.GetTempPath(), "source.rs");
		string prose = Path.Combine(Path.GetTempPath(), "notes.md");

		var mapped = LspFileChanges.FromInvalidations([
			new FileInvalidation(prose, FileInvalidationKind.Changed),
			new FileInvalidation(source, FileInvalidationKind.Deleted),
			new FileInvalidation(python, FileInvalidationKind.Created),
			new FileInvalidation(rust, FileInvalidationKind.Changed),
		]);

		Assert.Collection(mapped,
			change => {
				Assert.Equal(new Uri(source).AbsoluteUri, change.Uri);
				Assert.Equal(FileChangeKind.Deleted, change.Kind);
			},
			change => {
				Assert.Equal(new Uri(python).AbsoluteUri, change.Uri);
				Assert.Equal(FileChangeKind.Created, change.Kind);
			},
			change => {
				Assert.Equal(new Uri(rust).AbsoluteUri, change.Uri);
				Assert.Equal(FileChangeKind.Changed, change.Kind);
			});
	}
}
