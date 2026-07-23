using Weavie.Core.Processes;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// The one-shot runner against the real platform shell: output + exit-code capture, environment layering,
/// and the unstartable-tool case reporting as a failed run rather than a throw.
/// </summary>
public sealed class ToolProcessTests {
	private static ToolProcessRequest Shell(string command, IReadOnlyDictionary<string, string> environment) =>
		new(
			OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
			[OperatingSystem.IsWindows() ? "/c" : "-c", command],
			environment,
			Path.GetTempPath());

	[Fact]
	public async Task CapturesOutput_AndLayersEnvironment() {
		string echo = OperatingSystem.IsWindows() ? "echo %WEAVIE_TOOL_TEST%" : "echo $WEAVIE_TOOL_TEST";
		var result = await ToolProcess.RunAsync(
			Shell(echo, new Dictionary<string, string> { { "WEAVIE_TOOL_TEST", "hello-env" } }), CancellationToken.None);

		Assert.Equal(0, result.ExitCode);
		Assert.Contains("hello-env", result.StdOut);
	}

	[Fact]
	public async Task NonZeroExit_IsReported() {
		var result = await ToolProcess.RunAsync(Shell("exit 3", new Dictionary<string, string>()), CancellationToken.None);

		Assert.Equal(3, result.ExitCode);
	}

	[Fact]
	public async Task Cancel_KillsTheTool_AndThrowsPromptly() {
		// The wait must not just be abandoned: the child (tree) is reaped, so the call returns promptly
		// instead of after the tool's natural 30s runtime.
		string wait = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 > nul" : "sleep 30";
		using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
		var started = DateTimeOffset.UtcNow;

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			ToolProcess.RunAsync(Shell(wait, new Dictionary<string, string>()), cts.Token));

		Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(15), "cancelled run should return promptly");
	}

	[Fact]
	public async Task UnstartableTool_ReportsFailedRun_NotThrow() {
		string missing = Path.Combine(Path.GetTempPath(), $"weavie-no-such-tool-{Guid.NewGuid():N}");
		var result = await ToolProcess.RunAsync(
			new ToolProcessRequest(missing, [], new Dictionary<string, string>(), Path.GetTempPath()),
			CancellationToken.None);

		Assert.Equal(-1, result.ExitCode);
		Assert.Contains("Unable to start", result.StdErr);
	}
}
