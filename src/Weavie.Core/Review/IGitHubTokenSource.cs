namespace Weavie.Core.Review;

/// <summary>
/// Resolves a GitHub API token for the <see cref="GitHubReviewProvider"/>. An implementation detail of the
/// GitHub provider (another forge brings its own), kept behind an interface so the provider can be tested
/// without spawning <c>gh</c> / <c>git</c>. Returns <c>null</c> when no credential is available — the caller
/// surfaces a "connect GitHub" affordance rather than failing silently.
/// </summary>
public interface IGitHubTokenSource {
	/// <summary>The resolved token, or <c>null</c> when none was found.</summary>
	Task<string?> GetTokenAsync(CancellationToken ct = default);
}
