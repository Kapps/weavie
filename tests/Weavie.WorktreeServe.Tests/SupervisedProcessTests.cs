using System.Diagnostics;
using Xunit;

namespace Weavie.WorktreeServe.Tests;

public sealed class SupervisedProcessTests {
	[Fact]
	public async Task Completion_does_not_wait_for_pipe_handles_inherited_by_a_detached_descendant() {
		if (OperatingSystem.IsWindows()) {
			return;
		}
		var start = new ProcessStartInfo("/bin/sh") {
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		start.ArgumentList.Add("-c");
		start.ArgumentList.Add("(sleep 2) &");
		using var process = new SupervisedProcess("test", start, _ => { }, _ => { });

		process.Start();

		Assert.Equal(0, await process.Completion.WaitAsync(TimeSpan.FromSeconds(1)));
	}
}
