using System.Reflection;

namespace Weavie.Runner;

/// <summary>The running runner's build identity, stamped by the build (see Directory.Build.props).</summary>
internal static class RunnerIdentity {
	/// <summary>The SemVer build identity (e.g. <c>0.1.247</c>; local builds are <c>0.1.0</c>).</summary>
	public static string BuildNumber { get; } =
		typeof(RunnerIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
		?? throw new InvalidOperationException("Weavie.Runner has no AssemblyInformationalVersion — the build-stamp target did not run.");

	/// <summary>The integer build number (the SemVer patch; 0 for local builds).</summary>
	public static int Build { get; } = ParseBuild(BuildNumber);

	/// <summary>
	/// The spawn-contract generation this runner speaks (compiled in from <c>SpawnContractVersion</c>);
	/// a bundle declaring a newer generation is not applied until the runner restarts onto one that does.
	/// </summary>
	public static int SpawnContract { get; } =
		typeof(RunnerIdentity).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
			.FirstOrDefault(a => a.Key == "SpawnContract")?.Value is { } value && int.TryParse(value, out int parsed)
			? parsed
			: throw new InvalidOperationException("Weavie.Runner has no SpawnContract assembly metadata — the csproj stamp is missing.");

	/// <summary>Parses the integer build out of a <c>0.1.&lt;build&gt;</c> identity (a worker's or our own).</summary>
	public static int ParseBuild(string buildNumber) {
		ArgumentException.ThrowIfNullOrEmpty(buildNumber);
		string patch = buildNumber[(buildNumber.LastIndexOf('.') + 1)..];
		return int.TryParse(patch, out int build)
			? build
			: throw new FormatException($"'{buildNumber}' is not a 0.1.<build> build identity.");
	}
}
