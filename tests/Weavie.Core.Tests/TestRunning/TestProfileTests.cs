using Weavie.Core.TestRunning;
using Xunit;

namespace Weavie.Core.Tests.TestRunning;

/// <summary>Tests <see cref="TestProfile.TryParse"/>: the empty/`[]` distinction, multi-rule parsing, and rejection of malformed JSON, missing fields, and non-compiling regexes.</summary>
public sealed class TestProfileTests {
	[Fact]
	public void EmptyString_ParsesToEmptyProfile() {
		Assert.True(TestProfile.TryParse("", out var profile, out _));
		Assert.Empty(profile.Rules);
	}

	[Fact]
	public void EmptyArray_ParsesToEmptyProfile() {
		Assert.True(TestProfile.TryParse("[]", out var profile, out _));
		Assert.Empty(profile.Rules);
	}

	[Fact]
	public void MultiRule_ParsesEachRule_PreservingOrderAndOptionalFields() {
		const string json = """
			[
			  { "glob": "**/*.test.ts", "symbol": "^(?:describe|it)\\('(.+?)'", "runOne": "vitest ${file} -t ${name}", "runFile": "vitest ${file}", "nameSeparator": " > " },
			  { "glob": "**/*Tests.cs", "symbol": "^(\\w+)\\(", "runOne": "dotnet test", "runFile": "dotnet test", "header": "\\[(Fact|Theory)\\b" }
			]
			""";
		Assert.True(TestProfile.TryParse(json, out var profile, out string error), error);

		Assert.Equal(2, profile.Rules.Count);
		Assert.Equal("**/*.test.ts", profile.Rules[0].Glob);
		Assert.Equal(" > ", profile.Rules[0].NameSeparator);
		Assert.Null(profile.Rules[0].Header);
		Assert.Equal(" ", profile.Rules[1].NameSeparator); // default
		Assert.Equal("\\[(Fact|Theory)\\b", profile.Rules[1].Header);
	}

	[Fact]
	public void MalformedJson_Fails() {
		Assert.False(TestProfile.TryParse("[ {not json ]", out _, out string error));
		Assert.Contains("valid JSON", error, StringComparison.Ordinal);
	}

	[Fact]
	public void NonArray_Fails() {
		Assert.False(TestProfile.TryParse("{}", out _, out string error));
		Assert.Contains("array", error, StringComparison.Ordinal);
	}

	[Fact]
	public void MissingRequiredField_Fails_NamingFieldAndIndex() {
		const string json = """[{ "glob": "*.ts", "symbol": "x", "runOne": "a" }]"""; // no runFile
		Assert.False(TestProfile.TryParse(json, out _, out string error));
		Assert.Contains("runFile", error, StringComparison.Ordinal);
		Assert.Contains("[0]", error, StringComparison.Ordinal);
	}

	[Fact]
	public void EmptyRequiredField_Fails() {
		const string json = """[{ "glob": "", "symbol": "x", "runOne": "a", "runFile": "b" }]""";
		Assert.False(TestProfile.TryParse(json, out _, out string error));
		Assert.Contains("glob", error, StringComparison.Ordinal);
	}

	[Fact]
	public void BadSymbolRegex_Fails() {
		const string json = """[{ "glob": "*.ts", "symbol": "(unclosed", "runOne": "a", "runFile": "b" }]""";
		Assert.False(TestProfile.TryParse(json, out _, out string error));
		Assert.Contains("symbol", error, StringComparison.Ordinal);
		Assert.Contains("regex", error, StringComparison.Ordinal);
	}

	[Fact]
	public void BadHeaderRegex_Fails() {
		const string json = """[{ "glob": "*.cs", "symbol": "x", "runOne": "a", "runFile": "b", "header": "[bad" }]""";
		Assert.False(TestProfile.TryParse(json, out _, out string error));
		Assert.Contains("header", error, StringComparison.Ordinal);
	}

	[Fact]
	public void Validate_MirrorsTryParse() {
		Assert.True(TestProfile.Validate("[]").IsValid);
		Assert.False(TestProfile.Validate("{}").IsValid);
	}
}
