using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Weavie.Core.Terminal;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class WindowsConPtyTerminalTests {
	[Fact]
	public void Dispose_KillsTheAttachedProcessTree() {
		if (!OperatingSystem.IsWindows()) {
			return;
		}

		using var term = new WindowsConPtyTerminal();
		var output = new StringBuilder();
		object outputGate = new();
		term.Output += bytes => {
			lock (outputGate) {
				output.Append(Encoding.UTF8.GetString(bytes));
			}
		};
		term.Start(new TerminalStartInfo {
			Command = "powershell.exe",
			Arguments = [
				"-NoProfile",
				"-Command",
				"$child = Start-Process ping.exe -ArgumentList '-t','127.0.0.1' -PassThru; "
					+ "Write-Output \"CHILD:$($child.Id)\"; Wait-Process -Id $child.Id",
			],
		});

		int childPid = 0;
		Assert.True(SpinWait.SpinUntil(() => {
			lock (outputGate) {
				var match = Regex.Match(output.ToString(), @"CHILD:(\d+)");
				return match.Success && int.TryParse(match.Groups[1].Value, out childPid);
			}
		}, TimeSpan.FromSeconds(10)), "child process id was not reported");

		term.Dispose();

		Assert.True(SpinWait.SpinUntil(() => !ProcessIsAlive(childPid), TimeSpan.FromSeconds(5)),
			$"child process {childPid} survived terminal disposal");
	}

	private static bool ProcessIsAlive(int pid) {
		try {
			using var process = Process.GetProcessById(pid);
			return !process.HasExited;
		} catch (ArgumentException) {
			return false;
		}
	}
}
