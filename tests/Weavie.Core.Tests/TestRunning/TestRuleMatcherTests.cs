using Weavie.Core.TestRunning;
using Xunit;

namespace Weavie.Core.Tests.TestRunning;

/// <summary>Tests <see cref="TestRuleMatcher"/>: glob translation (**, *, ?, {a,b}, ?(x)) and first-match rule selection across a multi-language profile.</summary>
public sealed class TestRuleMatcherTests {
	private static TestRule Rule(string glob) =>
		new() { Glob = glob, Symbol = "x", RunOne = "o", RunFile = "f" };

	[Theory]
	[InlineData("**/*.test.ts?(x)", "a.test.ts", true)] // **/ matches zero leading directories
	[InlineData("**/*.test.ts?(x)", "src/a.test.ts", true)]
	[InlineData("**/*.test.ts?(x)", "src/deep/nested/a.test.tsx", true)]
	[InlineData("**/*.test.ts?(x)", "src/a.test.tsz", false)]
	[InlineData("**/*.test.ts?(x)", "src/a.ts", false)]
	[InlineData("**/*_test.go", "pkg/x_test.go", true)]
	[InlineData("**/*_test.go", "pkg/x.go", false)]
	[InlineData("**/*Tests.cs", "src/FooTests.cs", true)]
	[InlineData("**/*.{spec,test}.js", "a.spec.js", true)]
	[InlineData("**/*.{spec,test}.js", "a.test.js", true)]
	[InlineData("**/*.{spec,test}.js", "a.unit.js", false)]
	[InlineData("*.ts", "a.ts", true)]
	[InlineData("*.ts", "sub/a.ts", false)] // single star does not cross a separator
	public void GlobMatch(string glob, string path, bool expected) {
		var profile = new TestProfile { Rules = [Rule(glob)] };
		Assert.Equal(expected, TestRuleMatcher.Match(profile, path) is not null);
	}

	[Fact]
	public void FirstMatchingRuleWins_AcrossLanguages() {
		var profile = new TestProfile {
			Rules = [
				new TestRule { Glob = "**/*_test.go", Symbol = "^Test", RunOne = "go", RunFile = "go" },
				new TestRule { Glob = "**/*.test.ts", Symbol = "^it", RunOne = "vitest", RunFile = "vitest" },
			],
		};
		Assert.Equal("vitest", TestRuleMatcher.Match(profile, "web/a.test.ts")!.RunFile);
		Assert.Equal("go", TestRuleMatcher.Match(profile, "svc/a_test.go")!.RunFile);
		Assert.Null(TestRuleMatcher.Match(profile, "README.md"));
	}

	[Fact]
	public void BackslashPaths_AreNormalized() {
		var profile = new TestProfile { Rules = [Rule("**/*.test.ts")] };
		Assert.NotNull(TestRuleMatcher.Match(profile, "src\\win\\a.test.ts"));
	}
}
