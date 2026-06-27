using System.ComponentModel;
using System.Diagnostics;
using System.Text;

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
		var (exit, stdout, _) = await RunAsync("gh", ["auth", "token"], stdin: null, ct).ConfigureAwait(false);
		return exit == 0 && stdout.Trim() is { Length: > 0 } token ? token : null;
	}

	// `git credential fill` consults the configured helper (osxkeychain / manager / libsecret) for github.com
	// and prints `key=value` lines; the token is the `password`.
	private static async Task<string?> TryGitCredentialAsync(CancellationToken ct) {
		var (exit, stdout, _) = await RunAsync(
			"git", ["credential", "fill"], stdin: $"protocol=https\nhost={Host}\n\n", ct).ConfigureAwait(false);
		if (exit != 0) {
			return null;
		}

		foreach (string line in stdout.Split('\n')) {
			if (line.StartsWith("password=", StringComparison.Ordinal)) {
				string token = line["password=".Length..].Trim('\r', ' ');
				return token.Length > 0 ? token : null;
			}
		}

		return null;
	}

	private static async Task<(int Exit, string StdOut, string StdErr)> RunAsync(
		string file, IReadOnlyList<string> args, string? stdin, CancellationToken ct) {
		var info = new ProcessStartInfo {
			FileName = file,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			RedirectStandardInput = stdin is not null,
			UseShellExecute = false,
			CreateNoWindow = true,
			StandardOutputEncoding = Encoding.UTF8,
		};
		foreach (string arg in args) {
			info.ArgumentList.Add(arg);
		}

		using var process = new Process { StartInfo = info };
		try {
			process.Start();
		} catch (Win32Exception) {
			return (-1, string.Empty, string.Empty); // tool not installed — fall through to the next source
		}

		if (stdin is not null) {
			await process.StandardInput.WriteAsync(stdin.AsMemory(), ct).ConfigureAwait(false);
			process.StandardInput.Close();
		}

		string stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
		string stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
		await process.WaitForExitAsync(ct).ConfigureAwait(false);
		return (process.ExitCode, stdout, stderr);
	}
}
