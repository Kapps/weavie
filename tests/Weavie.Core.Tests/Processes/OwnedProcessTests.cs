using System.Diagnostics;
using Weavie.Core.Processes;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class OwnedProcessTests {
	[Fact]
	public async Task StreamsAndExitStatusBelongToTheActualCommand() {
		if (OperatingSystem.IsWindows()) return;
		var info = new ProcessStartInfo("/bin/sh") {
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		info.ArgumentList.Add("-c");
		info.ArgumentList.Add("read value; printf '%s' \"$value\"; printf 'error' >&2; exit 7");
		using var child = OwnedProcess.Start(info);
		await child.StandardInput.WriteLineAsync("input");
		child.StandardInput.Close();
		var output = child.StandardOutput.ReadToEndAsync();
		var error = child.StandardError.ReadToEndAsync();
		await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Equal("input", await output);
		Assert.Equal("error", await error);
		Assert.Equal(7, child.ExitCode);
		child.Kill(entireProcessTree: true);
	}

	[Fact]
	public async Task DisposingAfterKillPreservesPendingExitWait() {
		if (OperatingSystem.IsWindows()) return;
		var info = new ProcessStartInfo("/bin/sh") {
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		info.ArgumentList.Add("-c");
		info.ArgumentList.Add("printf 'ready\\n'; exec sleep 300");
		using var child = OwnedProcess.Start(info);
		Assert.Equal("ready", await child.StandardOutput.ReadLineAsync());
		var exit = child.WaitForExitAsync();
		child.Kill(entireProcessTree: true);
		child.Dispose();
		await exit.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.True(child.HasExited);
		Assert.NotEqual(0, child.ExitCode);
	}

	[Fact]
	public void MissingCommandIsAStartFailure() {
		var info = new ProcessStartInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))) {
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		Assert.Throws<System.ComponentModel.Win32Exception>(() => OwnedProcess.Start(info));
	}
}
