using Xunit;

namespace Weavie.Workflows.Tests;

public sealed class WorkflowFilePathsTests {
	[Fact]
	public void NormalizationIsLexicalAndWorksWithTrailingRootSeparators() {
		using var dir = new TempDirectory("weavie-workflow-paths");
		Assert.True(WorkflowFilePaths.TryResolve(dir.Path + Path.DirectorySeparatorChar, "docs/../review.md", out string relative, out string full));
		Assert.Equal("review.md", relative);
		Assert.Equal(dir.Combine("review.md"), full);
		Assert.False(File.Exists(full));
	}

	[Theory]
	[InlineData("../outside.md")]
	[InlineData("docs/../../outside.md")]
	[InlineData(".")]
	[InlineData("\u0000")]
	[InlineData("")]
	public void InvalidOrEscapingPathsAreRejectedWithoutThrowing(string path) =>
		Assert.False(WorkflowFilePaths.TryResolve(Path.GetTempPath(), path, out _, out _));

	[Fact]
	public void AbsolutePathsAreRejectedEvenInsideTheWorktree() =>
		Assert.False(WorkflowFilePaths.TryResolve(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "file.md"), out _, out _));
}
