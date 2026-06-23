using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// <see cref="LoginShellEnvironment"/> recovers the user's environment from a login-shell probe: extracting the
/// fenced body out of arbitrary rc-file noise, parsing the NUL-delimited vars, and taking the shell environment
/// as authoritative bar transient session noise. The probe itself is a no-op off a bundled Mac app, so importing
/// never disturbs a terminal/dev launch.
/// </summary>
public sealed class LoginShellEnvironmentTests {
	[Fact]
	public void ExtractFenced_PullsBody_IgnoringSurroundingNoise() =>
		Assert.Equal(
			"PATH=/usr/bin\0DOTNET_ROOT=/opt/dotnet\0",
			LoginShellEnvironment.ExtractFenced(
				"Welcome!\n__WEAVIE_ENV_BEGIN__PATH=/usr/bin\0DOTNET_ROOT=/opt/dotnet\0__WEAVIE_ENV_END__\n"));

	[Fact]
	public void ExtractFenced_ReturnsNull_WhenMarkersMissing() =>
		Assert.Null(LoginShellEnvironment.ExtractFenced("rc file printed nothing useful"));

	[Fact]
	public void ParseEnv_SplitsPairs_AndToleratesEqualsInValues() {
		var pairs = LoginShellEnvironment.ParseEnv("PATH=/usr/bin\0LS_COLORS=di=34:ln=35\0bogus\0");
		Assert.Equal(
			[new("PATH", "/usr/bin"), new("LS_COLORS", "di=34:ln=35")],
			pairs);
	}

	[Fact]
	public void ResolveImports_TakesEveryShellVar_ExceptSessionNoise() {
		var shell = new KeyValuePair<string, string>[] {
			new("PATH", "/opt/homebrew/bin:/usr/bin"),
			new("DOTNET_ROOT", "/opt/dotnet"),
			new("HOME", "/from/shell"),
			new("SHLVL", "1"),
			new("PWD", "/somewhere"),
		};

		var imports = LoginShellEnvironment.ResolveImports(shell);

		Assert.Equal(
			[
				new("PATH", "/opt/homebrew/bin:/usr/bin"),
				new("DOTNET_ROOT", "/opt/dotnet"),
				new("HOME", "/from/shell"), // shell is authoritative — taken as-is, not filtered
			],
			imports); // SHLVL and PWD dropped as transient session noise
	}

	[Fact]
	public async Task ImportOnceAsync_LeavesEnvironmentUntouched_OffABundledMacApp() {
		// The test host is not a bundled .app, so the import must be inert — no shell spawned, PATH unchanged.
		string? before = Environment.GetEnvironmentVariable("PATH");
		await LoginShellEnvironment.ImportOnceAsync(_ => Assert.Fail("should not log when skipped"));
		Assert.Equal(before, Environment.GetEnvironmentVariable("PATH"));
	}
}
