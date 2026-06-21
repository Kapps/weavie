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
	public void Catalog_ResolvesTypeScriptByLanguageId() {
		Assert.NotNull(LanguageServerCatalog.ForLanguage("typescript"));
		Assert.NotNull(LanguageServerCatalog.ForLanguage("javascriptreact"));
		Assert.Equal("typescript", LanguageServerCatalog.ForServerId("typescript")?.Id);
		Assert.Null(LanguageServerCatalog.ForLanguage("rust"));
	}
}
