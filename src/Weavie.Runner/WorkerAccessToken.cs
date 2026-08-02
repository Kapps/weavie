using System.Security.Cryptography;
using System.Text;

namespace Weavie.Runner;

/// <summary>Derives the stable, role-separated credential for one runner workspace.</summary>
internal static class WorkerAccessToken {
	private const string Purpose = "weavie/worker-access-token/v1\0";

	public static string Derive(string runnerToken, string workspaceRoot) {
		ArgumentException.ThrowIfNullOrEmpty(runnerToken);
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		byte[] key = Encoding.UTF8.GetBytes(runnerToken);
		byte[] message = Encoding.UTF8.GetBytes(Purpose + Normalize(workspaceRoot));
		byte[] digest = HMACSHA256.HashData(key, message);
		return Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
	}

	private static string Normalize(string workspaceRoot) {
		string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
		return OperatingSystem.IsWindows()
			? normalized.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).ToUpperInvariant()
			: normalized;
	}
}
