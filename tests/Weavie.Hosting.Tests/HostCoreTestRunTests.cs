using Weavie.Core;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// Drives the <c>weavie.tests.*</c> Core handlers end-to-end through a real <see cref="HostCore"/>: a configured
/// profile composes a command into the shell PTY; an unset profile and a busy shell both fail loudly and write
/// nothing. The profile uses <c>echo</c> templates (pure data — no framework knowledge in the test).
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreTestRunTests {
	private const string Profile =
		"test.profile = '[{\"glob\":\"**/*.test.ts\",\"symbol\":\"^(?:it|test)\\\\(\",\"runOne\":\"echo RUN ${file} -t ${name}\",\"runFile\":\"echo RUN ${file}\"}]'\n";

	[Fact]
	public async Task RunFile_ComposesCommand_IntoShellPane() {
		await using var host = await TestHost.StartAsync(repo => WriteProfile(repo, Profile));
		string file = Path.Combine(host.RepoRoot, "a.test.ts");
		host.SelectedSession.Shell.EnsureStarted();
		var shell = Assert.Single(host.Platform.NoopLauncher.Created);

		var result = await host.InvokeClientCommandAsync("weavie.tests.runFile", new { file });

		Assert.True(result.Ok, result.Error);
		Assert.Equal($"echo RUN '{file}'\r", shell.WrittenText);
	}

	[Fact]
	public async Task RunOne_ComposesQuotedName() {
		await using var host = await TestHost.StartAsync(repo => WriteProfile(repo, Profile));
		string file = Path.Combine(host.RepoRoot, "a.test.ts");
		host.SelectedSession.Shell.EnsureStarted();
		var shell = Assert.Single(host.Platform.NoopLauncher.Created);

		var result = await host.InvokeClientCommandAsync(
			"weavie.tests.run",
			new { file, name = "adds two" });

		Assert.True(result.Ok, result.Error);
		Assert.Equal($"echo RUN '{file}' -t 'adds two'\r", shell.WrittenText);
	}

	[Fact]
	public async Task NoProfile_FailsLoudly_AndWritesNothing() {
		await using var host = await TestHost.StartAsync(); // no test profile configured
		string file = Path.Combine(host.RepoRoot, "a.test.ts");
		host.SelectedSession.Shell.EnsureStarted();
		var shell = Assert.Single(host.Platform.NoopLauncher.Created);

		var result = await host.InvokeClientCommandAsync("weavie.tests.runFile", new { file });

		Assert.False(result.Ok);
		Assert.Contains("No test profile", result.Error, StringComparison.Ordinal);
		Assert.Equal(string.Empty, shell.WrittenText);
	}

	[Fact]
	public async Task BusyShell_FailsLoudly_AndWritesNothing() {
		await using var host = await TestHost.StartAsync(repo => WriteProfile(repo, Profile));
		string file = Path.Combine(host.RepoRoot, "a.test.ts");
		host.SelectedSession.Shell.EnsureStarted();
		var shell = Assert.Single(host.Platform.NoopLauncher.Created);
		shell.HasForegroundJob = true;

		var result = await host.InvokeClientCommandAsync("weavie.tests.runFile", new { file });

		Assert.False(result.Ok);
		Assert.Contains("busy", result.Error, StringComparison.Ordinal);
		Assert.Equal(string.Empty, shell.WrittenText);
	}

	[Fact]
	public async Task FileOutsideTheWorktree_NamesTheRealReason() {
		// The editor opens files anywhere, but test rules are globs over checkout-relative paths — so say that
		// rather than blaming the profile for not matching "../../notes.md".
		await using var host = await TestHost.StartAsync(repo => WriteProfile(repo, Profile));
		string file = Path.Combine(Path.GetDirectoryName(host.RepoRoot)!, "outside.md");
		await File.WriteAllTextAsync(file, "x");

		var result = await host.InvokeClientCommandAsync("weavie.tests.runFile", new { file });

		Assert.False(result.Ok);
		Assert.Contains("isn't inside", result.Error, StringComparison.Ordinal);
		Assert.DoesNotContain("No test rule", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task UnmatchedFile_FailsLoudly() {
		await using var host = await TestHost.StartAsync(repo => WriteProfile(repo, Profile));
		string file = Path.Combine(host.RepoRoot, "notes.md");

		var result = await host.InvokeClientCommandAsync("weavie.tests.runFile", new { file });

		Assert.False(result.Ok);
		Assert.Contains("No test rule", result.Error, StringComparison.Ordinal);
	}

	private static void WriteProfile(string repo, string profileLine) {
		// Workspace settings live out-of-repo, keyed by path (WEAVIE_ROOT is redirected under the test root).
		string overlay = WeaviePaths.WorkspaceSettingsFile(WorkspaceId.ForPath(repo));
		Directory.CreateDirectory(Path.GetDirectoryName(overlay)!);
		File.WriteAllText(overlay, profileLine);
	}
}
