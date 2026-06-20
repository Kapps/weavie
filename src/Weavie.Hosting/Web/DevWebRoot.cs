// Debug-only — Release has no Vite dev server, so the resolver is compiled out to stay dead-code-free.
#if DEBUG
using System.Reflection;

namespace Weavie.Hosting.Web;

/// <summary>Resolves the web source directory the Debug build injects as <c>WeavieWebDevDir</c> assembly
/// metadata (see each host csproj). The host passes <em>its own</em> assembly — the metadata lives on the host
/// exe, not on shared Weavie.Hosting — so the read stays at the host boundary while the logic is shared.</summary>
public static class DevWebRoot {
	/// <summary>The <c>WeavieWebDevDir</c> from <paramref name="hostAssembly"/>, normalized to a full path that exists, or <c>null</c>.</summary>
	public static string? Resolve(Assembly hostAssembly) {
		ArgumentNullException.ThrowIfNull(hostAssembly);

		string? raw = hostAssembly
			.GetCustomAttributes<AssemblyMetadataAttribute>()
			.FirstOrDefault(a => string.Equals(a.Key, "WeavieWebDevDir", StringComparison.Ordinal))
			?.Value;
		if (string.IsNullOrEmpty(raw)) {
			return null;
		}

		string full = Path.GetFullPath(raw);
		return Directory.Exists(full) ? full : null;
	}
}
#endif
