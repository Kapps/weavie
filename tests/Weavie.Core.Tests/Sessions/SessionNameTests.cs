using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Exercises <see cref="SessionName.Title"/>.</summary>
public sealed class SessionNameTests {
	[Theory]
	[InlineData("feature/review-front-door", "review front door")]
	[InlineData("main", "main")]
	[InlineData("kapps/a/c-d", "c d")]
	[InlineData("fix/snake_case--name", "snake case name")]
	[InlineData("bug/trailing/", "trailing")]
	[InlineData("-", "-")]
	public void ReadsTheBranchLeaf(string label, string title) =>
		Assert.Equal(title, SessionName.Title(label));
}
