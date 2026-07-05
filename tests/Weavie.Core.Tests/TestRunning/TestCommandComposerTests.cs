using Weavie.Core.TestRunning;
using Xunit;

namespace Weavie.Core.Tests.TestRunning;

/// <summary>Tests <see cref="TestCommandComposer"/>: placeholder substitution, POSIX/PowerShell quoting including injection attempts, and rejection of unknown/unavailable placeholders.</summary>
public sealed class TestCommandComposerTests {
	private static TestRule Rule(string runOne, string runFile) =>
		new() { Glob = "*", Symbol = "x", RunOne = runOne, RunFile = runFile };

	[Fact]
	public void RunOne_SubstitutesFileDirAndName_QuotedPosix() {
		var rule = Rule("vitest run ${file} -t ${name}", "vitest run ${file}");
		Assert.True(TestCommandComposer.TryCompose(
			rule, TestCommandKind.RunOne, "/repo/a.test.ts", "adds two", ShellQuoting.Posix, out string cmd, out string error), error);
		Assert.Equal("vitest run '/repo/a.test.ts' -t 'adds two'", cmd);
	}

	[Fact]
	public void RunFile_SubstitutesFileDir() {
		var rule = Rule("go test ${fileDir} -run ${name}", "go test ${fileDir}");
		Assert.True(TestCommandComposer.TryCompose(
			rule, TestCommandKind.RunFile, "/repo/pkg/x_test.go", null, ShellQuoting.Posix, out string cmd, out _));
		Assert.Equal("go test '/repo/pkg'", cmd);
	}

	[Fact]
	public void RunFile_SubstitutesFileName_ForClassScopedDotnet() {
		// C# runFile scopes to the file's class by convention via ${fileName} (base name, no extension).
		var rule = Rule("dotnet test --filter FullyQualifiedName~${name}", "dotnet test --filter FullyQualifiedName~${fileName}");
		Assert.True(TestCommandComposer.TryCompose(
			rule, TestCommandKind.RunFile, "/repo/tests/MathTests.cs", null, ShellQuoting.Posix, out string cmd, out string error), error);
		Assert.Equal("dotnet test --filter FullyQualifiedName~'MathTests'", cmd);
	}

	[Fact]
	public void RunOne_CSharpFilter_ComposerQuotesName_NoLiteralQuotesInTemplate() {
		// The composer single-quotes ${name}; the template must not wrap it in quotes of its own (a filter value
		// like FullyQualifiedName~'Adds' → the shell strips the quotes → FullyQualifiedName~Adds, matching).
		var rule = Rule("dotnet test --filter FullyQualifiedName~${name}", "dotnet test");
		Assert.True(TestCommandComposer.TryCompose(
			rule, TestCommandKind.RunOne, "/r/MathTests.cs", "Adds", ShellQuoting.Posix, out string cmd, out _));
		Assert.Equal("dotnet test --filter FullyQualifiedName~'Adds'", cmd);
	}

	[Fact]
	public void PosixQuoting_NeutralizesInjectionInName() {
		var rule = Rule("vitest -t ${name}", "vitest");
		// A name carrying a shell metacharacter payload must be quoted, not interpreted.
		const string evil = "'; rm -rf / #";
		Assert.True(TestCommandComposer.TryCompose(
			rule, TestCommandKind.RunOne, "/r/a.ts", evil, ShellQuoting.Posix, out string cmd, out _));
		Assert.Equal("vitest -t ''\\''; rm -rf / #'", cmd);
		Assert.DoesNotContain("; rm -rf / #\"", cmd, StringComparison.Ordinal); // payload stays inside quotes
	}

	[Fact]
	public void PowerShellQuoting_DoublesSingleQuotes() {
		var rule = Rule("Invoke-Pester -t ${name}", "Invoke-Pester");
		Assert.True(TestCommandComposer.TryCompose(
			rule, TestCommandKind.RunOne, "C:/r/a.ts", "it's fine", ShellQuoting.PowerShell, out string cmd, out _));
		Assert.Equal("Invoke-Pester -t 'it''s fine'", cmd);
	}

	[Fact]
	public void NameInRunFileTemplate_Fails() {
		// RunFile has no test name; a ${name} in that template is a loud error, not a silent blank.
		var rule = Rule("vitest ${file} -t ${name}", "vitest ${file} -t ${name}");
		Assert.False(TestCommandComposer.TryCompose(
			rule, TestCommandKind.RunFile, "/r/a.ts", null, ShellQuoting.Posix, out _, out string error));
		Assert.Contains("${name}", error, StringComparison.Ordinal);
	}

	[Fact]
	public void UnknownPlaceholder_Fails() {
		var rule = Rule("vitest ${bogus}", "vitest ${bogus}");
		Assert.False(TestCommandComposer.TryCompose(
			rule, TestCommandKind.RunFile, "/r/a.ts", null, ShellQuoting.Posix, out _, out string error));
		Assert.Contains("bogus", error, StringComparison.Ordinal);
	}
}
