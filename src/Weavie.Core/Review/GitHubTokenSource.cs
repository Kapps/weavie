using Weavie.Core.Processes;

namespace Weavie.Core.Review;

/// <summary>
/// Discovers a GitHub token from the credentials a developer machine (or a headless worker) already has, in
/// precedence order: the <c>GITHUB_TOKEN</c>/<c>GH_TOKEN</c> environment (server provisioning / CI), then
/// <c>gh auth token</c> (the GitHub CLI's store), then <c>git credential fill</c> (the OS credential helper the
/// user already pushes with). This is the zero-config path; the interactive OAuth flow lands later with the
/// source system. Each step is best-effort — a missing tool or non-zero exit just falls through.
/// </summary>
public sealed class GitHubTokenSource : IGitHubTokenSource {
	private const string Host = "github.com";

	/// <inheritdoc/>
	public async Task<string?> GetTokenAsync(CancellationToken ct = default) {
		foreach (string name in (string[])["GITHUB_TOKEN", "GH_TOKEN"]) {
			string? value = Environment.GetEnvironmentVariable(name);
			if (!string.IsNullOrWhiteSpace(value)) {
				return value.Trim();
			}
		}

		string? fromGh = await TryGhAsync(ct).ConfigureAwait(false);
		if (fromGh is not null) {
			return fromGh;
		}

		return await TryGitCredentialAsync(ct).ConfigureAwait(false);
	}

	// `gh auth token` prints the CLI's stored token to stdout (exit 0), or errors when unauthenticated.
	private static async Task<string?> TryGhAsync(CancellationToken ct) {
		var result = await RunAsync("gh", ["auth", "token"], string.Empty, ct).ConfigureAwait(false);
		return result.ExitCode == 0 && result.StdOut.Trim() is { Length: > 0 } token ? token : null;
	}

	// `git credential fill` consults the configured helper (osxkeychain / manager / libsecret) for github.com
	// and prints `key=value` lines; the token is the `password`.
	private static async Task<string?> TryGitCredentialAsync(CancellationToken ct) {
		var result = await RunAsync(
			"git", ["credential", "fill"], $"protocol=https\nhost={Host}\n\n", ct).ConfigureAwait(false);
		if (result.ExitCode != 0) {
			return null;
		}

		foreach (string line in result.StdOut.Split('\n')) {
			if (line.StartsWith("password=", StringComparison.Ordinal)) {
				string token = line["password=".Length..].Trim('\r', ' ');
				return token.Length > 0 ? token : null;
			}
		}

		return null;
	}

	// A tool that is not installed is this machine's answer for that source, not a failure — the -1 exit it comes
	// back with simply falls through to the next one.
	private static Task<ProcessCaptureResult> RunAsync(
		string file, IReadOnlyList<string> args, string stdin, CancellationToken ct) =>
		ProcessCapture.RunAsync(
			new ProcessCaptureRequest { FileName = file, Arguments = args, StandardInput = stdin }, ct);
}
