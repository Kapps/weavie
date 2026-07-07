using Weavie.Core.Agents;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>Platform PTY launchers render neutral launches without provider branches.</summary>
public sealed class PtyLauncherTests {
	private static AgentLaunch Launch(string command, AgentExecutableMode mode) => new() {
		Command = command,
		Arguments = ["--flag", "value"],
		WorkingDirectory = "/repo",
		RemoveEnvironment = ["SECRET"],
		Environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["X"] = "1" },
		ExecutableMode = mode,
		WorkingDirectoryMode = AgentWorkingDirectoryMode.Fixed,
		OutputCapture = new AgentOutputCapture.Disabled(),
	};

	[Fact]
	public void Windows_CommandShim_PreservesArgumentsAndEnvironment() {
		var resolved = new WindowsPtyLauncher().Resolve(Launch("agent.cmd", AgentExecutableMode.LoginShell));

		Assert.Equal(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", resolved.Command);
		Assert.Equal(["/c", "agent.cmd", "--flag", "value"], resolved.Arguments);
		Assert.Equal(["SECRET"], resolved.RemoveEnvironment);
		Assert.Equal("1", resolved.Environment["X"]);
	}

	[Fact]
	public void Posix_LoginShell_WrapsLogicalCommandAndAddsTerminalEnvironment() {
		var resolved = new PosixPtyLauncher().Resolve(Launch("agent", AgentExecutableMode.LoginShell));

		Assert.Equal(["-l", "-i", "-c", "exec 'agent' --flag 'value'"], resolved.Arguments);
		Assert.Equal("xterm-256color", resolved.Environment["TERM"]);
		Assert.Equal("truecolor", resolved.Environment["COLORTERM"]);
		Assert.Equal("1", resolved.Environment["X"]);
	}
}
