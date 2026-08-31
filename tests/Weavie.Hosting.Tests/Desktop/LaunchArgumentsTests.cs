using Weavie.Hosting.Desktop;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>What every host reads its argv through, including the URIs a desktop entry's %U hands over.</summary>
public sealed class LaunchArgumentsTests {
	[Fact]
	public void ABarePathBecomesAbsolute() {
		var parsed = LaunchArguments.Parse(["notes.md"]);

		Assert.Equal(Path.GetFullPath("notes.md"), Assert.Single(parsed.Paths));
	}

	[Fact]
	public void AFileUriBecomesItsLocalPath() {
		// A desktop entry declaring %U hands over URIs, not paths.
		var parsed = LaunchArguments.Parse(["file:///tmp/weavie%20notes.md"]);

		Assert.Equal("/tmp/weavie notes.md", Assert.Single(parsed.Paths));
	}

	[Fact]
	public void NamedOptionsAreReadWithoutBecomingPaths() {
		var parsed = LaunchArguments.Parse(["--port", "8700", "--workspace", "/repo"]);

		Assert.Empty(parsed.Paths);
		Assert.Equal("8700", parsed.Option("port"));
		Assert.Equal("/repo", parsed.Option("workspace"));
	}

	[Fact]
	public void AValuelessFlagIsRecordedWithoutSwallowingTheNextOption() {
		var parsed = LaunchArguments.Parse(["--pty-smoke", "--port", "8700"]);

		Assert.Equal(string.Empty, parsed.Option("pty-smoke"));
		Assert.Equal("8700", parsed.Option("port"));
	}

	[Fact]
	public void OptionsAndPathsMix() {
		var parsed = LaunchArguments.Parse(["--port", "8700", "/tmp/a.ts", "/tmp/b.ts"]);

		Assert.Equal(["/tmp/a.ts", "/tmp/b.ts"], parsed.Paths);
		Assert.Equal("8700", parsed.Option("port"));
	}

	[Fact]
	public void NoArgumentsIsNoPaths() => Assert.Empty(LaunchArguments.Parse([]).Paths);
}
