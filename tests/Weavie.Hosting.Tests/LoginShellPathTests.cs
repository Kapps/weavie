using Weavie.Hosting;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// <see cref="LoginShellPath"/> recovers the user's PATH from a login-shell probe: extracting the fenced value
/// out of arbitrary rc-file noise, and unioning it ahead of the inherited PATH without dropping or duplicating
/// entries. The probe itself is a no-op off a bundled Mac app, so importing never disturbs a terminal/dev launch.
/// </summary>
public sealed class LoginShellPathTests {
	[Fact]
	public void ExtractFenced_PullsValue_IgnoringSurroundingNoise() =>
		Assert.Equal(
			"/opt/homebrew/bin:/usr/bin",
			LoginShellPath.ExtractFenced("Welcome!\n__WEAVIE_PATH_BEGIN__/opt/homebrew/bin:/usr/bin__WEAVIE_PATH_END__\n"));

	[Fact]
	public void ExtractFenced_ReturnsNull_WhenMarkersMissing() =>
		Assert.Null(LoginShellPath.ExtractFenced("rc file printed nothing useful"));

	[Fact]
	public void Merge_PrependsShellPath_AndDeduplicates() =>
		Assert.Equal(
			"/opt/homebrew/bin:/usr/bin:/bin",
			LoginShellPath.Merge("/opt/homebrew/bin:/usr/bin", "/usr/bin:/bin"));

	[Fact]
	public void Merge_KeepsInheritedEntries_WhenShellPathIsSubset() =>
		Assert.Equal("/usr/bin:/sbin", LoginShellPath.Merge("/usr/bin", "/usr/bin:/sbin"));

	[Fact]
	public async Task ImportOnceAsync_LeavesPathUntouched_OffABundledMacApp() {
		// The test host is not a bundled .app, so the import must be inert — no shell spawned, PATH unchanged.
		string? before = Environment.GetEnvironmentVariable("PATH");
		await LoginShellPath.ImportOnceAsync(_ => Assert.Fail("should not log when skipped"));
		Assert.Equal(before, Environment.GetEnvironmentVariable("PATH"));
	}
}
