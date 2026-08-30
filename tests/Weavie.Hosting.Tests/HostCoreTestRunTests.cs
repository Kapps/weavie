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
		host.SelectedSession.Shells.Primary!.Controller.EnsureStarted();
		var shell = Assert.Single(host.Platform.NoopLauncher.Created);

		var result = await host.InvokeClientCommandAsync("weavie.tests.runFile", new { file });

		Assert.True(result.Ok, result.Error);
		Assert.Equal($"echo RUN '{file}'\r", shell.WrittenText);
		var focus = host.Bridge.LastEvent(host.SelectedSession.Address, "view", "focusPane")!.Value;
		Assert.Equal("terminal:shell", focus.GetProperty("kind").GetString());
		Assert.Equal(host.SelectedSession.Shells.Primary!.Id, focus.GetProperty("terminalId").GetString());
	}

	[Fact]
	public async Task ExitedPrimaryShell_FailsLoudly_AndWritesNothing() {
		await using var host = await TestHost.StartAsync(repo => WriteProfile(repo, Profile));
		string file = Path.Combine(host.RepoRoot, "a.test.ts");
		host.SelectedSession.Shells.Primary!.Controller.EnsureStarted();
		var shell = Assert.Single(host.Platform.NoopLauncher.Created);
		shell.Exit(0);
		await Wait.UntilAsync(() => !host.SelectedSession.Shells.Primary!.Controller.IsRunning);

		var result = await host.InvokeClientCommandAsync("weavie.tests.runFile", new { file });

		Assert.False(result.Ok);
		Assert.Contains("exited", result.Error, StringComparison.Ordinal);
		Assert.Equal(string.Empty, shell.WrittenText);
	}

	[Fact]
	public async Task RunOne_ComposesQuotedName() {
		await using var host = await TestHost.StartAsync(repo => WriteProfile(repo, Profile));
		string file = Path.Combine(host.RepoRoot, "a.test.ts");
		host.SelectedSession.Shells.Primary!.Controller.EnsureStarted();
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
		host.SelectedSession.Shells.Primary!.Controller.EnsureStarted();
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
		host.SelectedSession.Shells.Primary!.Controller.EnsureStarted();
		var shell = Assert.Single(host.Platform.NoopLauncher.Created);
		shell.HasForegroundJob = true;

		var result = await host.InvokeClientCommandAsync("weavie.tests.runFile", new { file });

		Assert.False(result.Ok);
		Assert.Contains("busy", result.Error, StringComparison.Ordinal);
		Assert.Equal(string.Empty, shell.WrittenText);
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
