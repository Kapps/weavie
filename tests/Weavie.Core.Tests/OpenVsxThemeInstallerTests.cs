using Weavie.Core.Theming;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Verifies the pure parsing the Open VSX theme installer relies on (metadata → download URL/version,
/// and a VS Code manifest → contributed themes with resolved paths). The networked install is covered
/// by running the LSP/theme tooling against the real registry, not in these hermetic unit tests.
/// </summary>
public sealed class OpenVsxThemeInstallerTests {
	[Fact]
	public void ParseMetadata_ExtractsDownloadUrlAndVersion() {
		const string json = """
		{ "name": "theme-dracula", "version": "2.24.0",
		  "files": { "download": "https://open-vsx.org/api/dracula-theme/theme-dracula/2.24.0/file/x.vsix" } }
		""";

		var (download, version) = OpenVsxThemeInstaller.ParseMetadata(json);
		Assert.Equal("https://open-vsx.org/api/dracula-theme/theme-dracula/2.24.0/file/x.vsix", download);
		Assert.Equal("2.24.0", version);
	}

	[Fact]
	public void ParseMetadata_ReturnsNulls_WhenNoDownload() {
		var (download, version) = OpenVsxThemeInstaller.ParseMetadata("""{ "version": "1.0.0" }""");
		Assert.Null(download);
		Assert.Equal("1.0.0", version);
	}

	[Fact]
	public void ParseThemeContributions_ResolvesLabelUiThemeAndAbsolutePath() {
		var extensionDir = Path.Combine(Path.GetTempPath(), "weavie-ext");
		const string packageJson = """
		{ "contributes": { "themes": [
		  { "label": "Dracula", "uiTheme": "vs-dark", "path": "./theme/dracula.json" },
		  { "label": "Dracula Soft", "uiTheme": "vs-dark", "path": "theme/dracula-soft.json" }
		] } }
		""";

		var contributions = OpenVsxThemeInstaller.ParseThemeContributions(packageJson, extensionDir);

		Assert.Equal(2, contributions.Count);
		Assert.Equal("Dracula", contributions[0].Label);
		Assert.Equal("vs-dark", contributions[0].UiTheme);
		Assert.Equal(Path.GetFullPath(Path.Combine(extensionDir, "theme/dracula.json")), contributions[0].Path);
		Assert.Equal("Dracula Soft", contributions[1].Label);
	}

	[Fact]
	public void ParseThemeContributions_FallsBackToIdThenFilename() {
		var contributions = OpenVsxThemeInstaller.ParseThemeContributions(
			"""{ "contributes": { "themes": [ { "path": "./themes/My Cool.json" } ] } }""",
			Path.GetTempPath());
		Assert.Single(contributions);
		Assert.Equal("My Cool", contributions[0].Label);
		Assert.Equal("vs-dark", contributions[0].UiTheme);
	}

	[Fact]
	public void ParseThemeContributions_EmptyWhenNoThemes() {
		Assert.Empty(OpenVsxThemeInstaller.ParseThemeContributions("""{ "contributes": {} }""", Path.GetTempPath()));
		Assert.Empty(OpenVsxThemeInstaller.ParseThemeContributions("{}", Path.GetTempPath()));
	}
}
