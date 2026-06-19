using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Exercises <see cref="SessionIdentity"/>'s deterministic hue + monogram derivation.</summary>
public sealed class SessionIdentityTests {
	[Fact]
	public void Hue_IsStableAndInRange() {
		int first = SessionIdentity.Hue("feature/auth");
		int second = SessionIdentity.Hue("feature/auth");

		Assert.Equal(first, second);
		Assert.InRange(first, 0, 359);
	}

	[Fact]
	public void Hue_VariesAcrossLabels() {
		string[] labels = ["a", "b", "c", "d", "e", "f", "g", "h"];
		int distinct = labels.Select(SessionIdentity.Hue).Distinct().Count();
		Assert.True(distinct > 1);
	}

	[Theory]
	[InlineData("main", "MA")]
	[InlineData("wip", "WI")]
	[InlineData("feature/auth-refactor", "AR")]
	[InlineData("feature/auth", "AU")]
	[InlineData("a", "A")]
	public void Monogram_Cases(string label, string expected) =>
		Assert.Equal(expected, SessionIdentity.Monogram(label));
}
