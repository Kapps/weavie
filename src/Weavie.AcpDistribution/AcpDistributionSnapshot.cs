using System.Text.Json;
using Weavie.Core;
using Weavie.Core.FileSystem;

namespace Weavie.AcpDistribution;

/// <summary>Materializes an isolated, validated copy of the user's installed ACP provider catalog.</summary>
public static class AcpDistributionSnapshot {
	/// <summary>Strictly reads and cross-validates the installed and custom provider catalog without writing.</summary>
	public static IReadOnlyList<AcpLaunchSpec> ReadCatalog(string sourceRoot) {
		ArgumentException.ThrowIfNullOrEmpty(sourceRoot);
		string source = Path.GetFullPath(sourceRoot);
		var fileSystem = new LocalFileSystem();
		var installed = new AcpInstallationStore(
			fileSystem,
			Under(source, WeaviePaths.AcpInstallationsFile)).Load();
		var custom = new AcpCustomAgentStore(fileSystem, Under(source, WeaviePaths.AcpCustomAgentsFile)).Load();
		ValidateUniqueIds(installed, custom);
		return [.. installed, .. custom];
	}

	/// <summary>
	/// Copies launch recipes from <paramref name="sourceRoot"/> into <paramref name="destinationRoot"/>, copying
	/// binary packages as regular files and rewriting their command paths to the destination.
	/// </summary>
	public static IReadOnlyList<AcpLaunchSpec> Materialize(string sourceRoot, string destinationRoot) {
		ArgumentException.ThrowIfNullOrEmpty(sourceRoot);
		ArgumentException.ThrowIfNullOrEmpty(destinationRoot);
		string source = Path.GetFullPath(sourceRoot);
		string destination = Path.GetFullPath(destinationRoot);
		var fileSystem = new LocalFileSystem();
		var catalog = ReadCatalog(source);
		var installed = catalog.Where(agent => agent.Distribution != "custom").ToArray();
		var custom = catalog.Where(agent => agent.Distribution == "custom").ToArray();

		string sourcePackages = Under(source, WeaviePaths.AcpPackages);
		string destinationPackages = Under(destination, WeaviePaths.AcpPackages);
		var projected = installed.Select(agent => agent.Distribution == "binary"
			? MaterializeBinary(agent, sourcePackages, destinationPackages, destination)
			: agent).ToArray();

		new AcpInstallationStore(fileSystem, Under(destination, WeaviePaths.AcpInstallationsFile)).Save(projected);
		new AcpCustomAgentStore(fileSystem, Under(destination, WeaviePaths.AcpCustomAgentsFile)).Save(custom);
		return [.. projected, .. custom];
	}

	private static AcpLaunchSpec MaterializeBinary(
		AcpLaunchSpec agent,
		string sourcePackages,
		string destinationPackages,
		string destinationRoot) {
		string id = RequireSegment(agent.Id, "agent id");
		string version = RequireSegment(agent.Version, $"agent '{id}' version");
		string relativeInstall = Path.Combine(id, version, AcpPlatformTarget.Current());
		string sourceInstall = Path.Combine(sourcePackages, relativeInstall);
		if (!PathBoundary.Contains(
			PhysicalPath.Resolve(sourceInstall),
			PhysicalPath.Resolve(agent.Command),
			PhysicalPath.Comparison)) {
			throw new JsonException($"ACP binary installation '{id}' escapes its package directory.");
		}
		string destinationInstall = Path.Combine(destinationPackages, relativeInstall);
		FileTreeSnapshot.MirrorDirectory(sourceInstall, destinationInstall, destinationRoot);
		string command = Path.Combine(destinationInstall, Path.GetRelativePath(sourceInstall, agent.Command));
		if (!File.Exists(command)) {
			throw new JsonException($"ACP binary installation '{id}' is missing its command.");
		}
		return agent with { Command = command };
	}

	private static void ValidateUniqueIds(
		IReadOnlyList<AcpLaunchSpec> installed,
		IReadOnlyList<AcpLaunchSpec> custom) {
		var ids = new HashSet<string>(StringComparer.Ordinal);
		foreach (var agent in installed.Concat(custom)) {
			if (!ids.Add(agent.Id)) {
				throw new JsonException($"ACP provider '{agent.Id}' is configured more than once.");
			}
		}
	}

	private static string RequireSegment(string? value, string name) {
		if (string.IsNullOrWhiteSpace(value)
			|| value is "." or ".."
			|| value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
			|| value.Contains(Path.DirectorySeparatorChar)
			|| value.Contains(Path.AltDirectorySeparatorChar)) {
			throw new JsonException($"ACP {name} is not a safe path segment.");
		}
		return value;
	}

	private static string Under(string root, string canonicalPath) =>
		Path.Combine(root, Path.GetRelativePath(WeaviePaths.Root, canonicalPath));
}
