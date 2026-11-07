using Weavie.Core.Mcp;
using Xunit;

namespace Weavie.Core.Tests.Mcp;

public sealed class AuthorWorkflowPromptTests {
	[Theory]
	[InlineData("the entry `listWorkflows` shows with no `shadowedBy`")]
	[InlineData("folder `listWorkflows` reports")]
	[InlineData("Fix every error")]
	[InlineData("never assume a folder")]
	[InlineData("pass the complete proposed YAML to `validateWorkflow` as `text`")]
	[InlineData("not a script")]
	[InlineData("call `forkWorkflow` first")]
	[InlineData("never edit a built-in in place")]
	[InlineData("a variant under a new id is a new workflow, not a fork")]
	public void TeachesTheEditingRules(string rule) =>
		Assert.Contains(rule, Normalized(AuthorWorkflowPrompt.Prompt.Text), StringComparison.Ordinal);

	[Fact]
	public void NeverAssumesAFolder() => Assert.DoesNotContain(".weavie/", AuthorWorkflowPrompt.Prompt.Text, StringComparison.Ordinal);

	private static string Normalized(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
