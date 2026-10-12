using System.Diagnostics;
using Weavie.Core.Processes;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>A real .NET child's runtime IPC files land in its owned temp and go with it after a SIGKILL.</summary>
public sealed class ChildTempRuntimeTests {
	[Fact]
	public async Task KilledDotnetChildLeavesNoRuntimeFilesBehind() {
		if (OperatingSystem.IsWindows()) return;
		var info = new ProcessStartInfo(AcpAgentSessionFixture.ExecutablePath("tools", "Weavie.FakeAcp", "weavie-fake-acp")) {
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		info.ArgumentList.Add("lsp");
		using var child = OwnedProcess.Start(info);
		string[] patterns = [$"clr-debug-pipe-{child.Id}-*", $"dotnet-diagnostic-{child.Id}-*"];

		string[] runtimeFiles = await Task.WhenAll(patterns.Select(AppearsAsync));
		string temp = Assert.Single(runtimeFiles.Select(Path.GetDirectoryName).Distinct())!;
		Assert.Equal(ChildTemp.Root, Path.GetDirectoryName(temp));

		child.Kill(entireProcessTree: true);
		await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

		Assert.False(Directory.Exists(temp));
		Assert.Empty(RuntimeFiles(Path.GetTempPath(), patterns));
	}

	private static async Task<string> AppearsAsync(string pattern) {
		using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		while (true) {
			if (RuntimeFiles(ChildTemp.Root, [pattern]).FirstOrDefault() is { } path) return path;
			await Task.Delay(50, deadline.Token);
		}
	}

	private static IEnumerable<string> RuntimeFiles(string directory, string[] patterns) =>
		patterns.SelectMany(pattern => Directory.EnumerateFileSystemEntries(directory, pattern, SearchOption.AllDirectories));
}
