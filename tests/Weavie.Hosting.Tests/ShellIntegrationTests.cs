using Weavie.Hosting;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// Pins the launch the OSC 7 shell integration produces per shell, and that the rc files it materializes source
/// the user's own config while appending a cwd emitter — without needing a real PTY.
/// </summary>
public sealed class ShellIntegrationTests : IDisposable {
	private readonly string _root =
		Path.Combine(Path.GetTempPath(), "weavie-shellint-" + Guid.NewGuid().ToString("n"));
	private readonly ShellIntegration _integration;

	public ShellIntegrationTests() {
		_integration = new ShellIntegration(_root, "/home/tester");
	}

	[Fact]
	public void Bash_LaunchesViaRcfileThatEmitsOsc7AndSourcesLogin() {
		var launch = _integration.Resolve("bash");

		Assert.NotNull(launch);
		string rc = Path.Combine(_root, "weavie.bash");
		Assert.Equal(["--rcfile", rc, "-i"], launch!.Arguments);
		Assert.Empty(launch.Environment);

		string content = File.ReadAllText(rc);
		Assert.Contains("__weavie_osc7", content);
		Assert.Contains("]7;file://", content);
		Assert.Contains("PROMPT_COMMAND", content);
		Assert.Contains(".bash_profile", content); // reproduces the login startup the plain `bash -l` had
	}

	[Fact]
	public void Zsh_RedirectsZdotdirAndInjectsAPrecmdHookSourcingTheUserConfig() {
		var launch = _integration.Resolve("zsh");

		Assert.NotNull(launch);
		string zshDir = Path.Combine(_root, "zsh");
		Assert.Equal(["-l", "-i"], launch!.Arguments);
		Assert.Equal(zshDir, launch.Environment["ZDOTDIR"]);
		Assert.Equal("/home/tester", launch.Environment["WEAVIE_ZDOTDIR_USER"]);

		Assert.Contains("WEAVIE_ZDOTDIR_USER", File.ReadAllText(Path.Combine(zshDir, ".zshenv")));
		Assert.Contains("WEAVIE_ZDOTDIR_USER", File.ReadAllText(Path.Combine(zshDir, ".zprofile")));
		string zshrc = File.ReadAllText(Path.Combine(zshDir, ".zshrc"));
		Assert.Contains("add-zsh-hook precmd __weavie_osc7", zshrc);
		Assert.Contains("source \"$WEAVIE_ZDOTDIR_USER/.zshrc\"", zshrc); // user config still loads
	}

	[Theory]
	[InlineData("sh")]
	[InlineData("fish")]
	[InlineData("pwsh")]
	[InlineData("powershell")]
	public void UnsupportedShell_FallsBackToTheDefaultLaunch(string shell) =>
		Assert.Null(_integration.Resolve(shell));

	public void Dispose() {
		try {
			Directory.Delete(_root, recursive: true);
		} catch (IOException) {
			// best-effort temp cleanup
		}
	}
}
