using System.Text.Json;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// Drives the <c>weavie.tests.*</c> Core handlers end-to-end through a real <see cref="HostCore"/>: a configured
/// profile composes a command into the shell PTY; an unset profile and a busy shell both fail loudly and write
/// nothing. The profile uses <c>echo</c> templates (pure data — no framework knowledge in the test).
/// </summary>
public sealed class HostCoreTestRunTests {
	private const string Profile =
		"test.profile = '[{\"glob\":\"**/*.test.ts\",\"symbol\":\"^(?:it|test)\\\\(\",\"runOne\":\"echo RUN ${file} -t ${name}\",\"runFile\":\"echo RUN ${file}\"}]'\n";

	[Fact]
	public async Task RunFile_ComposesCommand_IntoShellPane() {
		await using var host = await TestHost.StartAsync(repo => WriteProfile(repo, Profile));
		string file = Path.Combine(host.RepoRoot, "a.test.ts");
		host.Core.ActiveSessionForTest()!.Shell.EnsureStarted();
		var shell = Assert.Single(host.Platform.NoopLauncher.Created);

		host.Send(Invoke("weavie.tests.runFile", "{\"file\":" + Str(file) + "}", token: null));

		Assert.Equal($"echo RUN '{file}'\r", shell.WrittenText);
	}

	[Fact]
	public async Task RunOne_ComposesQuotedName() {
		await using var host = await TestHost.StartAsync(repo => WriteProfile(repo, Profile));
		string file = Path.Combine(host.RepoRoot, "a.test.ts");
		host.Core.ActiveSessionForTest()!.Shell.EnsureStarted();
		var shell = Assert.Single(host.Platform.NoopLauncher.Created);

		host.Send(Invoke("weavie.tests.run", "{\"file\":" + Str(file) + ",\"name\":\"adds two\"}", token: null));

		Assert.Equal($"echo RUN '{file}' -t 'adds two'\r", shell.WrittenText);
	}

	[Fact]
	public async Task NoProfile_FailsLoudly_AndWritesNothing() {
		await using var host = await TestHost.StartAsync(); // no .weavie/settings.toml
		string file = Path.Combine(host.RepoRoot, "a.test.ts");
		host.Core.ActiveSessionForTest()!.Shell.EnsureStarted();
		var shell = Assert.Single(host.Platform.NoopLauncher.Created);

		var result = InvokeForResult(host, "weavie.tests.runFile", "{\"file\":" + Str(file) + "}");

		Assert.False(result.GetProperty("ok").GetBoolean());
		Assert.Contains("No test profile", result.GetProperty("error").GetString()!, StringComparison.Ordinal);
		Assert.Equal(string.Empty, shell.WrittenText);
	}

	[Fact]
	public async Task BusyShell_FailsLoudly_AndWritesNothing() {
		await using var host = await TestHost.StartAsync(repo => WriteProfile(repo, Profile));
		string file = Path.Combine(host.RepoRoot, "a.test.ts");
		host.Core.ActiveSessionForTest()!.Shell.EnsureStarted();
		var shell = Assert.Single(host.Platform.NoopLauncher.Created);
		shell.HasForegroundJob = true;

		var result = InvokeForResult(host, "weavie.tests.runFile", "{\"file\":" + Str(file) + "}");

		Assert.False(result.GetProperty("ok").GetBoolean());
		Assert.Contains("busy", result.GetProperty("error").GetString()!, StringComparison.Ordinal);
		Assert.Equal(string.Empty, shell.WrittenText);
	}

	[Fact]
	public async Task UnmatchedFile_FailsLoudly() {
		await using var host = await TestHost.StartAsync(repo => WriteProfile(repo, Profile));
		string file = Path.Combine(host.RepoRoot, "notes.md");

		var result = InvokeForResult(host, "weavie.tests.runFile", "{\"file\":" + Str(file) + "}");

		Assert.False(result.GetProperty("ok").GetBoolean());
		Assert.Contains("No test rule", result.GetProperty("error").GetString()!, StringComparison.Ordinal);
	}

	private static void WriteProfile(string repo, string profileLine) {
		Directory.CreateDirectory(Path.Combine(repo, ".weavie"));
		File.WriteAllText(Path.Combine(repo, ".weavie", "settings.toml"), profileLine);
	}

	// Invokes a Core command with a token and returns the posted command-result (synchronous: the handlers and
	// the inline UI dispatcher complete before Send returns).
	private static JsonElement InvokeForResult(TestHost host, string id, string argsJson) {
		host.Send(Invoke(id, argsJson, token: "t1"));
		return host.Bridge.LastOfType("command-result")
			?? throw new InvalidOperationException("no command-result posted");
	}

	private static string Invoke(string id, string argsJson, string? token) {
		string tokenPart = token is null ? string.Empty : ",\"token\":" + Str(token);
		return "{\"type\":\"invoke-command\",\"id\":" + Str(id) + ",\"args\":" + argsJson + tokenPart + "}";
	}

	private static string Str(string value) => JsonSerializer.Serialize(value);
}
