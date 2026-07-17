using Weavie.Core.Lsp;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// The offer gate at its fake seams: an offer exists only for a descriptor that doesn't resolve, whose
/// candidate carries a recipe, and whose toolchain is on PATH — and it drops the moment resolution succeeds.
/// </summary>
public sealed class ServerInstallOffersTests {
	private static readonly ResolvedCommand Resolved = new("/fake/server", [], "/fake/server");

	private static Func<string, string?> PathWith(params string[] commands) =>
		cmd => commands.Contains(cmd) ? $"/fake/bin/{cmd}" : null;

	[Fact]
	public void TypeScriptMiss_WithNpm_OffersTsgo() {
		var offers = new ServerInstallOffers(PathWith("npm"), _ => null);

		Assert.True(offers.RecordUnresolved(LanguageServerCatalog.TypeScript));

		Assert.Contains("typescript", offers.ActiveIds);
		var offer = offers.For("typescript");
		Assert.NotNull(offer);
		Assert.Equal("tsgo", offer!.Candidate.Command);
		Assert.Equal("npm", offer.Recipe.Toolchain);
	}

	[Fact]
	public void ToolchainMissing_NoOffer() {
		var offers = new ServerInstallOffers(PathWith(), _ => null);

		Assert.False(offers.RecordUnresolved(LanguageServerCatalog.Go));

		Assert.Empty(offers.ActiveIds);
	}

	[Fact]
	public void DescriptorResolves_NoOffer() {
		// A race where the server appeared between the failed start and the record must not offer an install.
		var offers = new ServerInstallOffers(PathWith("go"), _ => Resolved);

		Assert.False(offers.RecordUnresolved(LanguageServerCatalog.Go));

		Assert.Empty(offers.ActiveIds);
	}

	[Fact]
	public void RepeatedMiss_IsIdempotent() {
		// The web pool retries a failed start ~5 times; only the first miss may report a change.
		var offers = new ServerInstallOffers(PathWith("go"), _ => null);

		Assert.True(offers.RecordUnresolved(LanguageServerCatalog.Go));
		Assert.False(offers.RecordUnresolved(LanguageServerCatalog.Go));

		Assert.Single(offers.ActiveIds);
	}

	[Fact]
	public void RecipelessDescriptor_NoOffer() {
		var descriptor = new LanguageServerDescriptor {
			Id = "bogus",
			DisplayName = "Bogus",
			LanguageIds = ["bogus"],
			FileExtensions = [".bogus"],
			Candidates = [new ServerLaunchCandidate("bogus-ls", [])],
		};
		var offers = new ServerInstallOffers(PathWith("npm", "dotnet", "go"), _ => null);

		Assert.False(offers.RecordUnresolved(descriptor));

		Assert.Empty(offers.ActiveIds);
	}

	[Fact]
	public void Recompute_DropsAnOfferThatNowResolves() {
		bool installed = false;
		var offers = new ServerInstallOffers(PathWith("go"), _ => installed ? Resolved : null);
		Assert.True(offers.RecordUnresolved(LanguageServerCatalog.Go));

		installed = true;

		Assert.True(offers.Recompute());
		Assert.Empty(offers.ActiveIds);
		Assert.False(offers.Recompute()); // nothing left to drop
	}
}
