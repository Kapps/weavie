using Weavie.Core.Lsp;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Bring-your-own server resolution: explicit-path handling and graceful "not installed" behavior.
/// PATH probing of a real server is covered by the LSP harness.
/// </summary>
public sealed class ServerResolverTests {
	[Fact]
	public void FindOnPath_ReturnsExplicitPath_WhenItExists() {
		string temp = Path.Combine(Path.GetTempPath(), $"weavie-resolver-{Guid.NewGuid():N}.exe");
		File.WriteAllText(temp, "stub");
		try {
			Assert.Equal(temp, ServerResolver.FindOnPath(temp));
		} finally {
			File.Delete(temp);
		}
	}

	[Fact]
	public void FindOnPath_ReturnsNull_ForExplicitMissingPath() {
		string missing = Path.Combine(Path.GetTempPath(), $"weavie-missing-{Guid.NewGuid():N}.exe");
		Assert.Null(ServerResolver.FindOnPath(missing));
	}

	[Fact]
	public void FindOnPath_ReturnsNull_ForUnknownCommand() =>
		Assert.Null(ServerResolver.FindOnPath("weavie-definitely-not-a-real-command-xyz"));

	[Fact]
	public void Resolve_ReturnsNull_WhenNoCandidateInstalled() {
		var descriptor = new LanguageServerDescriptor {
			Id = "bogus",
			DisplayName = "Bogus",
			LanguageIds = ["bogus"],
			FileExtensions = [".bogus"],
			Candidates = [new ServerLaunchCandidate("weavie-no-such-server-xyz", ["--stdio"])],
		};

		Assert.Null(ServerResolver.Resolve(descriptor));
	}

	[Fact]
	public void FindInDirectory_FindsTheCommand_OrReturnsNull() {
		string dir = Path.Combine(Path.GetTempPath(), $"weavie-resolver-dir-{Guid.NewGuid():N}");
		Directory.CreateDirectory(dir);
		string name = "weavie-dir-probe" + (OperatingSystem.IsWindows() ? ".exe" : "");
		File.WriteAllText(Path.Combine(dir, name), "stub");
		try {
			Assert.Equal(Path.Combine(dir, name), ServerResolver.FindInDirectory(dir, "weavie-dir-probe"));
			Assert.Null(ServerResolver.FindInDirectory(dir, "weavie-not-there"));
		} finally {
			Directory.Delete(dir, recursive: true);
		}
	}

	[Fact]
	public void Resolve_FallsBackToWeavieToolsDir_ForARecipeCandidate() {
		// A Weavie-installed server (in ~/.weavie/tools, here redirected by TestRoot) resolves with no PATH change.
		string command = $"weavie-tools-probe-{Guid.NewGuid():N}";
		var descriptor = new LanguageServerDescriptor {
			Id = "tools-probe",
			DisplayName = "Tools Probe",
			LanguageIds = ["toolsprobe"],
			FileExtensions = [".toolsprobe"],
			Candidates = [new ServerLaunchCandidate(command, ["--stdio"]) {
				Install = new ServerInstallRecipe("npm", "some-package"),
			}],
		};
		Assert.Null(ServerResolver.Resolve(descriptor));

		string binDir = ToolchainInstall.BinDir("npm");
		Directory.CreateDirectory(binDir);
		string path = Path.Combine(binDir, command + (OperatingSystem.IsWindows() ? ".exe" : ""));
		File.WriteAllText(path, "stub");
		try {
			var resolved = ServerResolver.Resolve(descriptor);
			Assert.NotNull(resolved);
			Assert.Equal(path, resolved!.ServerPath);
		} finally {
			File.Delete(path);
		}
	}

	[Fact]
	public void Catalog_ResolvesTypeScriptByLanguageId() {
		Assert.NotNull(LanguageServerCatalog.ForLanguage("typescript"));
		Assert.NotNull(LanguageServerCatalog.ForLanguage("javascriptreact"));
		Assert.Equal("typescript", LanguageServerCatalog.ForServerId("typescript")?.Id);
		Assert.Null(LanguageServerCatalog.ForLanguage("rust"));
	}
}
