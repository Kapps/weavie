using System.Diagnostics;
using Weavie.Core.Processes;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class OwnedProcessTests {
	[Fact]
	public async Task SuppliedEnvironmentOwnsPathLookupAndChildVariables() {
		if (OperatingSystem.IsWindows()) return;
		using var root = new TempDirectory();
		string bin = root.CreateDirectory("bin space");
		string blocked = root.CreateDirectory("blocked");
		root.WriteFile(Path.Combine("blocked", "agent"), "not executable");
		File.CreateSymbolicLink(Path.Combine(bin, "agent"), "/bin/sh");
		var info = PathProbe("agent", root.Path);
		info.Environment["PATH"] = string.Join(':', root.Combine("missing"), root.Combine("blocked", "agent"), blocked, bin);
		await AssertPathProbeAsync(info);
	}

	[Theory]
	[InlineData("bin space", "bin space")]
	[InlineData("", "")]
	[InlineData(":missing", "")]
	[InlineData("missing:", "")]
	public async Task PathEntriesResolveAgainstChildWorkingDirectory(string path, string bin) {
		if (OperatingSystem.IsWindows()) return;
		using var root = new TempDirectory();
		string directory = root.CreateDirectory(bin);
		File.CreateSymbolicLink(Path.Combine(directory, "agent"), "/bin/sh");
		var info = PathProbe("agent", root.Path);
		info.Environment["PATH"] = path;
		await AssertPathProbeAsync(info);
	}

	[Theory]
	[InlineData("link/../bin/agent", "", "")]
	[InlineData("agent", "link/../bin", "")]
	[InlineData("agent", "bin", "link/..")]
	public async Task OperatingSystemResolvesParentSegmentsAcrossSymlinks(string command, string path, string directory) {
		if (OperatingSystem.IsWindows()) return;
		using var root = new TempDirectory();
		string target = root.CreateDirectory("real", "subdir");
		string bin = root.CreateDirectory("real", "bin");
		Directory.CreateSymbolicLink(root.Combine("link"), target);
		File.CreateSymbolicLink(Path.Combine(bin, "agent"), "/bin/sh");
		var info = PathProbe(command, Path.Combine(root.Path, directory));
		info.Environment["PATH"] = path;
		await AssertPathProbeAsync(info);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task QualifiedCommandBypassesPathAndUsesChildWorkingDirectory(bool absolute) {
		if (OperatingSystem.IsWindows()) return;
		using var root = new TempDirectory();
		string command = Path.Combine(root.CreateDirectory("bin"), "agent");
		File.CreateSymbolicLink(command, "/bin/sh");
		var info = PathProbe(absolute ? command : "./bin/agent", root.Path);
		info.Environment.Remove("PATH");
		await AssertPathProbeAsync(info);
	}

	[Theory]
	[InlineData(null, 2)]
	[InlineData("missing", 2)]
	[InlineData("blocked:missing", 13)]
	[InlineData("invalid", 8)]
	public void PathSearchPreservesLaunchFailures(string? path, int error) {
		if (OperatingSystem.IsWindows()) return;
		using var root = new TempDirectory();
		root.WriteFile(Path.Combine("blocked", "sh"), "not executable");
		string invalid = root.WriteFile(Path.Combine("invalid", "sh"), "not an executable format");
		File.SetUnixFileMode(invalid, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		var info = PathProbe("sh", root.Path);
		if (path is null) info.Environment.Remove("PATH");
		else info.Environment["PATH"] = path;

		var failure = Assert.Throws<System.ComponentModel.Win32Exception>(() => OwnedProcess.Start(info));

		Assert.Equal(error, failure.NativeErrorCode);
		Assert.Equal("sh", info.FileName);
	}

	private static ProcessStartInfo PathProbe(string command, string directory) {
		var info = new ProcessStartInfo(command) {
			WorkingDirectory = directory,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		info.ArgumentList.Add("-c");
		info.ArgumentList.Add("printf '%s\\n' \"$WEAVIE_PATH_PROBE\" \"$1\"; exit 7");
		info.ArgumentList.Add("probe");
		info.ArgumentList.Add("argument with spaces and 'quotes'");
		info.Environment["WEAVIE_PATH_PROBE"] = "child environment";
		return info;
	}

	private static async Task AssertPathProbeAsync(ProcessStartInfo info) {
		string command = info.FileName;
		using var child = OwnedProcess.Start(info);
		var output = child.StandardOutput.ReadToEndAsync();
		var error = child.StandardError.ReadToEndAsync();
		await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Equal("child environment\nargument with spaces and 'quotes'\n", await output);
		Assert.Equal(string.Empty, await error);
		Assert.Equal(7, child.ExitCode);
		Assert.Equal(command, info.FileName);
	}

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
