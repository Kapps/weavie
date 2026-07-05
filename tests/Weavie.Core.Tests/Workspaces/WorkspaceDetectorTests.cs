using Weavie.Core.FileSystem;
using Weavie.Core.TestRunning;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Core.Tests.Workspaces;

/// <summary>
/// Exercises <see cref="WorkspaceDetector"/>: per-language classification (package-manager + runner + framework
/// selection), the multi-language union + cd-wrapped setup chain, the runner-unknown gap, and the manifest gate
/// / bounded-walk behavior inherited from the old suggestion probe. Deterministic, over an in-memory tree.
/// </summary>
public sealed class WorkspaceDetectorTests {
	private static WorkspaceDetection Detect(params (string Path, string Content)[] files) {
		var fs = new InMemoryFileSystem();
		foreach (var (path, content) in files) {
			fs.WriteAllText(path, content);
		}

		return WorkspaceDetector.Detect("/repo", fs);
	}

	private static TestRule OnlyRule(WorkspaceDetection d) => Assert.Single(d.TestRules);

	[Fact]
	public void Go_ModAtRoot_DownloadAndTestRule() {
		var d = Detect(("/repo/go.mod", "module x\n"));

		Assert.True(d.HasManifest);
		Assert.Equal("go mod download", d.SetupCommand);
		Assert.Equal(["Go"], d.ConfiguredLanguages);
		var rule = OnlyRule(d);
		Assert.Equal("**/*_test.go", rule.Glob);
		Assert.Equal("go test ${fileDir} -run '^${name}$'", rule.RunOne);
		Assert.Equal("go test ${fileDir}", rule.RunFile);
	}

	[Fact]
	public void TypeScript_Vitest_Pnpm() {
		var d = Detect(
			("/repo/pnpm-lock.yaml", ""),
			("/repo/package.json", "{\"devDependencies\":{\"vitest\":\"^1\"}}"));

		Assert.Equal("pnpm install", d.SetupCommand);
		var rule = OnlyRule(d);
		Assert.Equal("pnpm vitest run ${file}", rule.RunFile);
		Assert.Equal("pnpm vitest run ${file} -t ${name}", rule.RunOne);
		Assert.Equal(" > ", rule.NameSeparator);
	}

	[Fact]
	public void TypeScript_Jest_Npm_UsesNpx() {
		// No lockfile → npm baseline; npm runs local binaries through npx.
		var d = Detect(("/repo/package.json", "{\"dependencies\":{\"jest\":\"^29\"}}"));

		Assert.Equal("npm install", d.SetupCommand);
		Assert.Equal("npx jest ${file}", OnlyRule(d).RunFile);
	}

	[Fact]
	public void TypeScript_PackageManagerField_OverridesLockfile() {
		var d = Detect(
			("/repo/pnpm-lock.yaml", ""),
			("/repo/package.json", "{\"packageManager\":\"yarn@4.1.0\",\"devDependencies\":{\"vitest\":\"^1\"}}"));

		Assert.Equal("yarn install", d.SetupCommand);
		Assert.Equal("yarn vitest run ${file}", OnlyRule(d).RunFile);
	}

	[Fact]
	public void TypeScript_NoKnownRunner_IsAGap_SetupOnlyNoRules() {
		// Language recognized, runner not — install still configured, but no rules so the card hands tests to Claude.
		var d = Detect(("/repo/package.json", "{\"dependencies\":{\"react\":\"^18\"}}"));

		Assert.True(d.HasManifest);
		Assert.Equal("npm install", d.SetupCommand);
		Assert.Empty(d.TestRules);
	}

	[Fact]
	public void CSharp_Xunit_RestoreAndClassScopedRunFile() {
		var d = Detect(
			("/repo/App.slnx", ""),
			("/repo/App.csproj", "<Project><ItemGroup><PackageReference Include=\"xunit\" /></ItemGroup></Project>"));

		Assert.Equal("dotnet restore", d.SetupCommand);
		var rule = OnlyRule(d);
		Assert.Equal("**/*.cs", rule.Glob);
		Assert.Equal("\\[(Fact|Theory)\\b", rule.Header);
		Assert.Equal("dotnet test --filter FullyQualifiedName~${name}", rule.RunOne);
		Assert.Equal("dotnet test --filter FullyQualifiedName~${fileName}", rule.RunFile);
	}

	[Fact]
	public void CSharp_Nunit_UsesTestAttributeHeader() {
		var d = Detect(("/repo/App.csproj", "<Project><ItemGroup><PackageReference Include=\"NUnit\" /></ItemGroup></Project>"));

		Assert.Equal("\\[(Test|TestMethod)\\b", OnlyRule(d).Header);
	}

	[Fact]
	public void CSharp_SolutionRooted_NunitInSubproject_DetectedAcrossTheTree() {
		// The C# marker is a .slnx at the root; the test project (with the framework reference) is a level down.
		// Header detection must scan the whole walk, not just the marker directory, or it silently defaults to xUnit.
		var d = Detect(
			("/repo/App.slnx", ""),
			("/repo/tests/Tests.csproj", "<Project><ItemGroup><PackageReference Include=\"NUnit\" /></ItemGroup></Project>"));

		Assert.Equal("\\[(Test|TestMethod)\\b", OnlyRule(d).Header);
	}

	[Fact]
	public void ManifestInShellUnsafeSubdir_IsSkipped_ButManifestStillDetected() {
		// A directory whose name carries shell metacharacters can't be cd-wrapped into an auto-run command safely
		// (the setup command executes unattended on session create). Decline it — the card offers the Claude flow.
		var d = Detect(("/repo/we$(evil)b/package.json", "{\"devDependencies\":{\"vitest\":\"^1\"}}"));

		Assert.True(d.HasManifest);   // the card still shows
		Assert.Null(d.SetupCommand);  // but nothing is auto-composed from the unsafe path
		Assert.Empty(d.TestRules);
	}

	[Fact]
	public void CSharp_NoReadableFramework_DefaultsToXunit() {
		// A .slnx at root with no csproj alongside → default xUnit rather than no rule.
		var d = Detect(("/repo/App.slnx", ""));

		Assert.Equal("\\[(Fact|Theory)\\b", OnlyRule(d).Header);
	}

	[Fact]
	public void MultiLanguage_WeavieShape_ChainsSetupAndUnionsRules() {
		// Weavie's own layout: .NET at the root, pnpm/vitest two levels down under src/web.
		var d = Detect(
			("/repo/weavie.slnx", ""),
			("/repo/App.csproj", "<Project><PackageReference Include=\"xunit\" /></Project>"),
			("/repo/src/web/pnpm-lock.yaml", ""),
			("/repo/src/web/package.json", "{\"devDependencies\":{\"vitest\":\"^1\"}}"));

		// The cd path uses the OS-native separator (correct for the shell that will run it) — build it that way.
		string web = Path.Combine("src", "web");
		Assert.Equal($"dotnet restore && (cd {web} && pnpm install)", d.SetupCommand);
		Assert.Equal(["C#", "TypeScript / JavaScript"], d.ConfiguredLanguages);
		Assert.Equal(2, d.TestRules.Count);
		// C# is at the root (no cd); TS is cd-wrapped to its package dir.
		Assert.Equal("dotnet test --filter FullyQualifiedName~${name}", d.TestRules[0].RunOne);
		Assert.Equal($"(cd {web} && pnpm vitest run ${{file}})", d.TestRules[1].RunFile);
		Assert.Equal($"(cd {web} && pnpm vitest run ${{file}} -t ${{name}})", d.TestRules[1].RunOne);
	}

	[Fact]
	public void UnsupportedManifest_HasManifestButNothingToWrite() {
		// A Makefile-only repo: the card shows (manifest present), but detection writes nothing → Claude fallback.
		var d = Detect(("/repo/Makefile", "all:\n"));

		Assert.True(d.HasManifest);
		Assert.Null(d.SetupCommand);
		Assert.Empty(d.TestRules);
		Assert.Empty(d.ConfiguredLanguages);
	}

	[Fact]
	public void NoManifest_DetectsNothing() {
		var d = Detect(("/repo/readme.txt", "hi"));

		Assert.False(d.HasManifest);
		Assert.Null(d.SetupCommand);
		Assert.Empty(d.TestRules);
	}

	[Fact]
	public void ManifestTooDeep_NotDetected() {
		// Three levels down is past the shallow walk's reach (root + 2 levels).
		var d = Detect(("/repo/a/b/c/go.mod", "module x\n"));

		Assert.False(d.HasManifest);
		Assert.Null(d.SetupCommand);
	}

	[Fact]
	public void ManifestUnderSkippedDir_NotDetected() {
		var d = Detect(("/repo/node_modules/pkg/package.json", "{}"));

		Assert.False(d.HasManifest);
	}
}
