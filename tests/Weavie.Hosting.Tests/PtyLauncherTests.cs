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

		Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), resolved.Command);
		Assert.Equal(["/d", "/s", "/v:off", "/c", "agent.cmd", "--flag", "value"], resolved.Arguments);
		Assert.Equal(["SECRET"], resolved.RemoveEnvironment);
		Assert.Equal("1", resolved.Environment["X"]);
	}

	[Fact]
	public void Windows_CommandShim_EscapesNpmVersionCeilingsFromRedirection() {
		var launch = Launch("npx.cmd", AgentExecutableMode.Direct) with {
			Arguments = ["--yes", "@scope/agent@<=1.2.3"],
		};

		var resolved = new WindowsPtyLauncher().Resolve(launch);

		Assert.Equal(["/d", "/s", "/v:off", "/c", "npx.cmd", "--yes", "@scope/agent@^<=1.2.3"], resolved.Arguments);
	}

	[Fact]
	public void Posix_SearchPath_UsesTheSystemEnvironmentLauncherWithoutAShell() {
		var resolved = new PosixPtyLauncher().Resolve(Launch("npx", AgentExecutableMode.SearchPath));

		Assert.Equal("/usr/bin/env", resolved.Command);
		Assert.Equal(["npx", "--flag", "value"], resolved.Arguments);
	}

	[Fact]
	public void Posix_LoginShell_WrapsLogicalCommandAndAddsTerminalEnvironment() {
		var resolved = new PosixPtyLauncher().Resolve(Launch("agent", AgentExecutableMode.LoginShell));

		Assert.Equal(["-l", "-i", "-c", "exec 'agent' '--flag' 'value'"], resolved.Arguments);
		Assert.Equal("xterm-256color", resolved.Environment["TERM"]);
		Assert.Equal("truecolor", resolved.Environment["COLORTERM"]);
		Assert.Equal("1", resolved.Environment["X"]);
	}

	[Fact]
	public void Posix_LoginShell_KeepsAnArgumentWithQuotesAndNewlinesIntact() {
		// A session's opening prompt is a launch argument, so ordinary prose ("don't", newlines, $VAR) has to
		// survive the shell verbatim rather than break or expand inside the command it renders.
		var launch = Launch("agent", AgentExecutableMode.LoginShell) with {
			Arguments = ["don't $HOME\nsecond line"],
		};

		var resolved = new PosixPtyLauncher().Resolve(launch);

		Assert.Equal(
			["-l", "-i", "-c", "exec 'agent' 'don'\\''t $HOME\nsecond line'"],
			resolved.Arguments);
	}
}
