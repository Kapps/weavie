using Weavie.Core.Theming;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Verifies the Open VSX installer's pure parsing: metadata → download URL/version, and a VS Code
/// manifest → contributed themes with resolved paths. The networked install is not covered here.
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
		string extensionDir = Path.Combine(Path.GetTempPath(), "weavie-ext");
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

	[Fact]
	public void ParseExtensionIdentity_ReadsPublisherNameVersion() {
		const string packageJson = """
		{ "publisher": "dracula-theme", "name": "theme-dracula", "version": "2.24.0",
		  "contributes": { "themes": [] } }
		""";

		var (publisher, name, version) = OpenVsxThemeInstaller.ParseExtensionIdentity(packageJson);
		Assert.Equal("dracula-theme", publisher);
		Assert.Equal("theme-dracula", name);
		Assert.Equal("2.24.0", version);
	}

	[Fact]
	public void ParseExtensionIdentity_ThrowsWhenIncomplete() {
		// All three coordinates are required; any missing one is not a valid extension.
		Assert.Throws<InvalidOperationException>(() =>
			OpenVsxThemeInstaller.ParseExtensionIdentity("""{ "name": "theme-x", "version": "1.0.0" }"""));
		Assert.Throws<InvalidOperationException>(() =>
			OpenVsxThemeInstaller.ParseExtensionIdentity("""{ "publisher": "p", "version": "1.0.0" }"""));
		Assert.Throws<InvalidOperationException>(() =>
			OpenVsxThemeInstaller.ParseExtensionIdentity("""{ "publisher": "p", "name": "theme-x" }"""));
	}

	[Fact]
	public void ParseThemeContributions_DropsAPathEscapingTheExtensionDir() {
		string extensionDir = Path.Combine(Path.GetTempPath(), "weavie-ext");
		const string packageJson = """
		{ "contributes": { "themes": [
		  { "label": "Evil", "uiTheme": "vs-dark", "path": "../../../../etc/passwd" }
		] } }
		""";

		// A malicious manifest path that escapes the unpacked extension dir is dropped, not resolved.
		Assert.Empty(OpenVsxThemeInstaller.ParseThemeContributions(packageJson, extensionDir));
	}
}
