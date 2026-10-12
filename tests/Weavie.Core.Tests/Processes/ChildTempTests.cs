using System.Diagnostics;
using Weavie.Core.Processes;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class ChildTempTests {
	private static readonly string UserTemp = Path.TrimEndingDirectorySeparator(Path.GetTempPath());

	[Fact]
	public async Task ChildRunsInItsOwnTempWhichGoesWhenItExits() {
		using var child = OwnedProcess.Start(Shell(
			"printf '%s\\n' \"$TMPDIR\" \"$WEAVIE_USER_TEMP\"; touch \"$TMPDIR/left-behind\"",
			"echo %TMP%& echo %TEMP%& echo %WEAVIE_USER_TEMP%& echo x> %TMP%\\left-behind"));
		string[] lines = (await child.StandardOutput.ReadToEndAsync()).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

		string temp = lines[0];
		Assert.Equal(ChildTemp.Root, Path.GetDirectoryName(temp));
		Assert.Equal([temp, .. OperatingSystem.IsWindows() ? [temp] : Array.Empty<string>(), UserTemp], lines);
		Assert.Equal(0, child.ExitCode);
		Assert.False(Directory.Exists(temp));
	}

	[Fact]
	public async Task KilledChildKeepsItsTempWhileAliveAndLosesItOnceReaped() {
		if (OperatingSystem.IsWindows()) return;
		using var child = OwnedProcess.Start(Shell("touch \"$TMPDIR/left-behind\"; printf '%s\\n' \"$TMPDIR\"; exec sleep 300", ""));
		string temp = (await child.StandardOutput.ReadLineAsync())!;
		await Task.Delay(TimeSpan.FromMilliseconds(200));

		Assert.True(File.Exists(Path.Combine(temp, "left-behind")));
		child.Kill(entireProcessTree: false);
		await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
		Assert.False(Directory.Exists(temp));
	}

	[Fact]
	public void TerminalsGetTheUserTempBack() {
		string[] variables = OperatingSystem.IsWindows() ? ["TMP", "TEMP"] : ["TMPDIR"];
		var owned = variables.ToDictionary(name => name, _ => "/private");
		owned[ChildTemp.UserTempVariable] = "/user";
		var plain = variables.ToDictionary(name => name, _ => "/plain");

		ChildTemp.RestoreUserTemp(owned);
		ChildTemp.RestoreUserTemp(plain);

		Assert.Equal(variables.ToDictionary(name => name, _ => "/user"), owned);
		Assert.Equal(variables.ToDictionary(name => name, _ => "/plain"), plain);
	}

	[Fact]
	public void ReclaimRemovesOnlyRootsWhoseOwnerIsGone() {
		using var temp = new TempDirectory();
		int self = Environment.ProcessId;
		long started = ChildTemp.StartStamp(self)!.Value;
		string live = Root(temp, ChildTemp.RootName(self, started));
		string reused = Root(temp, ChildTemp.RootName(self, started + 1));
		string exited = Root(temp, ChildTemp.RootName(int.MaxValue, started));
		string[] unrelated = [Root(temp, "weavie-theme-preview-1"), Root(temp, $"weavie-{self}-x"), Root(temp, "other")];
		var log = new List<string>();

		ChildTemp.ReclaimOrphans(temp.Path, log.Add);

		Assert.All([live, .. unrelated], root => Assert.True(Directory.Exists(root)));
		Assert.False(Directory.Exists(reused));
		Assert.False(Directory.Exists(exited));
		Assert.Equal(2, log.Count);
	}

	[Fact]
	public void ReclaimLeavesALiveChildProcessRootAlone() {
		if (OperatingSystem.IsWindows()) return;
		using var temp = new TempDirectory();
		using var sleeper = Process.Start("sleep", "300");
		try {
			string root = Root(temp, ChildTemp.RootName(sleeper.Id, ChildTemp.StartStamp(sleeper.Id)!.Value));
			ChildTemp.ReclaimOrphans(temp.Path, _ => { });
			Assert.True(Directory.Exists(root));
		} finally {
			sleeper.Kill();
			sleeper.WaitForExit();
		}
	}

	private static string Root(TempDirectory temp, string name) {
		string root = temp.CreateDirectory(name);
		File.WriteAllText(Path.Combine(temp.CreateDirectory(name, "1"), "file"), "child output");
		return root;
	}

	private static ProcessStartInfo Shell(string unix, string windows) {
		var info = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh") {
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		if (OperatingSystem.IsWindows()) {
			info.ArgumentList.Add("/d");
			info.ArgumentList.Add("/c");
			info.ArgumentList.Add(windows);
		} else {
			info.ArgumentList.Add("-c");
			info.ArgumentList.Add(unix);
		}
		return info;
	}
}
