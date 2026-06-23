using Weavie.Core.Lsp;

namespace Weavie.Hosting;

/// <summary>
/// Points <c>DOTNET_ROOT</c>/<c>DOTNET_HOST_PATH</c> at the user's .NET install so .NET-based children that
/// locate the SDK through hostfxr/MSBuildLocator — notably <c>csharp-ls</c> — can find it. hostfxr resolves the
/// SDK from <c>DOTNET_ROOT</c>, not <c>PATH</c>, so a Finder-launched <c>.app</c> (whose environment never had
/// these set) yields "No .NET SDKs were found" even when <c>dotnet</c> itself is on <c>PATH</c>.
/// </summary>
public static class DotnetEnvironment {
	/// <summary>
	/// Derives <c>DOTNET_ROOT</c>/<c>DOTNET_HOST_PATH</c> from the <c>dotnet</c> on <c>PATH</c> when unset. A no-op
	/// when <c>DOTNET_ROOT</c> is already set, <c>dotnet</c> isn't on <c>PATH</c>, or its directory isn't a real
	/// .NET root — never overwriting a working setup with a guess. Call after the login-shell PATH import so the
	/// resolved <c>dotnet</c> matches a terminal launch.
	/// </summary>
	/// <param name="log">Sink for a one-line note of what was resolved, or why it was skipped.</param>
	public static void EnsureRootResolved(Action<string> log) {
		ArgumentNullException.ThrowIfNull(log);
		if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ROOT"))) {
			return;
		}

		string? onPath = ServerResolver.FindOnPath("dotnet");
		if (onPath is null) {
			return;
		}

		// Follow symlinks (e.g. a Homebrew shim) to the real muxer so its sibling sdk/shared/host dirs are the root.
		string host = ResolveFinalTarget(onPath);
		string? root = DeriveRoot(host, dir => Directory.Exists(dir));
		if (root is null) {
			log($"dotnet at {host} has no sibling sdk/; leaving DOTNET_ROOT unset");
			return;
		}

		Environment.SetEnvironmentVariable("DOTNET_ROOT", root);
		Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", host);
		log($"resolved DOTNET_ROOT={root}");
	}

	/// <summary>The dotnet root is the muxer's directory, accepted only if it holds an <c>sdk</c> folder.</summary>
	internal static string? DeriveRoot(string hostPath, Func<string, bool> sdkDirExists) {
		string? root = Path.GetDirectoryName(hostPath);
		if (string.IsNullOrEmpty(root) || !sdkDirExists(Path.Combine(root, "sdk"))) {
			return null;
		}

		return root;
	}

	private static string ResolveFinalTarget(string path) {
		try {
			return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
		} catch (IOException) {
			return path;
		}
	}
}
