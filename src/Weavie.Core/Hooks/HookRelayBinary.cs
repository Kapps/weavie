using Weavie.Core;

namespace Weavie.Core.Hooks;

/// <summary>
/// Resolves the standalone hook relay binary co-located with hosts that spawn embedded agents.
/// </summary>
public static class HookRelayBinary {
	/// <summary>The platform-specific relay executable name.</summary>
	public static string Name => OperatingSystem.IsWindows() ? "weavie-hook-relay.exe" : "weavie-hook-relay";

	/// <summary>Returns the relay path under <paramref name="baseDirectory"/>, honoring managed-runner layouts.</summary>
	/// <param name="baseDirectory">The host base directory.</param>
	public static string PathIn(string baseDirectory) {
		ArgumentException.ThrowIfNullOrEmpty(baseDirectory);
		return ManagedRunnerLayout.CurrentRelayPath(baseDirectory, Name)
			?? Path.Combine(baseDirectory, Name);
	}

	/// <summary>Throws a user-visible integration error when <paramref name="path"/> is not present.</summary>
	/// <param name="path">The resolved relay path.</param>
	public static void RequireExists(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		if (!File.Exists(path)) {
			throw new InvalidOperationException(
				$"Hook relay '{Name}' was not found at '{path}'. "
				+ "The build co-locates it (see HookRelay.targets); a Release build requires the NativeAOT C++ toolchain.");
		}
	}
}
