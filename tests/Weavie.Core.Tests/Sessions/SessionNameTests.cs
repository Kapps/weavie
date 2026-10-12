using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Exercises <see cref="SessionName.Parse"/>.</summary>
public sealed class SessionNameTests {
	[Theory]
	[InlineData("feature/review-front-door", "feature", "review front door")]
	[InlineData("main", null, "main")]
	[InlineData("a/b/c-d", "a/b", "c d")]
	[InlineData("fix/snake_case--name", "fix", "snake case name")]
	[InlineData("bug/trailing/", "bug", "trailing")]
	[InlineData("-", null, "-")]
	public void SplitsPrefixAndReadableTitle(string label, string? prefix, string title) =>
		Assert.Equal(new SessionName(prefix, title), SessionName.Parse(label));
}
