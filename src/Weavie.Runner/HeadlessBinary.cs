namespace Weavie.Runner;

internal static class HeadlessBinary {
	internal static IEnumerable<string> InDirectory(string directory) => [
		Path.Combine(directory, OperatingSystem.IsWindows() ? "Weavie.Headless.exe" : "Weavie.Headless"),
		Path.Combine(directory, "Weavie.Headless.dll"),
	];

	internal static IEnumerable<string> BundleCandidates(string directory) =>
		InDirectory(Path.Combine(directory, "worker")).Concat(InDirectory(directory));
}
