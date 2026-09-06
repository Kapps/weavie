using System.Reflection;

namespace Weavie.Runner;

/// <summary>The running runner's build identity, stamped by the build (see Directory.Build.props).</summary>
internal static class RunnerIdentity {
	/// <summary>The public version followed by the internal build number (e.g. <c>0.2.1.1507</c>).</summary>
	public static string BuildNumber { get; } =
		typeof(RunnerIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
		?? throw new InvalidOperationException("Weavie.Runner has no AssemblyInformationalVersion — the build-stamp target did not run.");

	/// <summary>The internal build number (0 for local builds).</summary>
	public static int Build { get; } = ParseBuild(BuildNumber);

	/// <summary>The exact spawn-contract generation this runner accepts.</summary>
	public static int SpawnContract { get; } =
		typeof(RunnerIdentity).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
			.FirstOrDefault(a => a.Key == "SpawnContract")?.Value is { } value && int.TryParse(value, out int parsed)
			? parsed
			: throw new InvalidOperationException("Weavie.Runner has no SpawnContract assembly metadata — the csproj stamp is missing.");

	/// <summary>Reads the internal build number from a four-component build identity.</summary>
	public static int ParseBuild(string buildNumber) {
		ArgumentNullException.ThrowIfNull(buildNumber);
		return Version.TryParse(buildNumber, out var version) && version.Revision >= 0
			? version.Revision
			: throw new FormatException($"'{buildNumber}' is not a major.minor.patch.build identity.");
	}
}
