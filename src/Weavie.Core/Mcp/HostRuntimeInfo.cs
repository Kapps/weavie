using System.Globalization;

namespace Weavie.Core.Mcp;

/// <summary>Which network surface the host process exposes.</summary>
public enum HostTransport {
	/// <summary>Loopback only — a locally-driven host.</summary>
	Local,

	/// <summary>A token-gated network bind — a remote worker.</summary>
	Remote,
}

/// <summary>
/// The running host's identity, surfaced to the embedded claude in its system-prompt appendix
/// (<see cref="EmbeddedClaudeGuidance.Compose"/>): how it's reached (<see cref="Transport"/>), whether it's a
/// runner-managed worker, and the build it actually loaded.
/// </summary>
public sealed record HostRuntimeInfo(HostTransport Transport, bool Managed, string Build) {
	/// <summary>
	/// Resolves the info for a host based at <paramref name="baseDir"/>: a managed worker reports the build number
	/// it loaded (from its own <c>versions/&lt;build&gt;/</c> path, never the <c>current</c> symlink); any other
	/// host reports <paramref name="devVersion"/>.
	/// </summary>
	public static HostRuntimeInfo Resolve(HostTransport transport, string baseDir, string devVersion) {
		ArgumentException.ThrowIfNullOrEmpty(baseDir);
		ArgumentException.ThrowIfNullOrEmpty(devVersion);
		return ManagedRunnerLayout.LoadedBuildNumber(baseDir) is { } build
			? new HostRuntimeInfo(transport, Managed: true, build.ToString(CultureInfo.InvariantCulture))
			: new HostRuntimeInfo(transport, Managed: false, devVersion);
	}
}
