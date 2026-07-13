using Weavie.Core.Configuration;

namespace Weavie.Hosting.Tests;

internal static class TestNode {
	public static string Command { get; } = Resolve();

	private static string Resolve() {
		if (ExecutableFinder.FindOnPath("node") is { } command) {
			return command;
		}
		if (OperatingSystem.IsWindows()) {
			throw new InvalidOperationException("The hosting tests require Node on PATH.");
		}

		string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		string[] roots = [
			Path.Combine(home, ".nvm", "versions", "node"),
			Path.Combine(home, ".config", "nvm", "versions", "node"),
		];
		return roots.Where(Directory.Exists)
			.SelectMany(Directory.EnumerateDirectories)
			.Select(directory => Path.Combine(directory, "bin", "node"))
			.Where(File.Exists)
			.OrderByDescending(File.GetLastWriteTimeUtc)
			.FirstOrDefault()
			?? throw new InvalidOperationException("The hosting tests require an installed Node runtime.");
	}
}
